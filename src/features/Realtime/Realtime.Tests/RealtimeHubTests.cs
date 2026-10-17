using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Pole.Features.Realtime.Application;
using Pole.Features.Realtime.Contracts;
using Pole.Features.Tenants.Contracts;
using Xunit;

namespace Pole.Features.Realtime.Tests;

public sealed class RealtimeHubTests
{
    [Fact]
    public async Task FailedReplacementReleasesEveryInterestAndClearsAcknowledgedSubscriptions()
    {
        const string tenant = "000000000000000000000001";
        var registry =
            new RealtimeTopicRegistry([new("fake.one", RealtimeScope.Tenant), new("fake.two", RealtimeScope.Tenant)],
                new());
        var access = new RealtimeAccess((id, user, _, _) =>
                Task.FromResult<LoadTenantMemberOutcome>(
                    new TenantMemberSnapshot(id, user, TenantMembershipRole.Viewer)),
            (id, _) => Task.FromResult<LoadTenantIdentityOutcome>(new TenantIdentitySnapshot(id, "name")),
            registry);
        var tickets = new MemoryTickets();
        await tickets.StoreAsync(new AuthenticationTicket(IdentityFixture.User(),
            new()
            {
                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(1)
            },
            RealtimeFeature.CookieScheme));
        var subscriptions = new MemorySubscriptions();
        var first = new FakeLifecycle("fake.one");
        var second = new FakeLifecycle("fake.two");
        var hub = new RealtimeHub(subscriptions, tickets, access, [first, second], TimeProvider.System)
        {
            Context = new CallerContext()
        };
        var acknowledgements = await hub.Subscribe([new("fake.one", tenant), new("fake.two", tenant)]);
        Assert.All(acknowledgements, acknowledgement => Assert.True(acknowledgement.Covered));
        Assert.Single(first.Interests);
        Assert.Single(second.Interests);
        second.FailSubscribe = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            hub.Subscribe([new("fake.one", tenant), new("fake.two", tenant)]));
        Assert.Empty(subscriptions.Connections);
        Assert.Empty(first.Interests);
        Assert.Empty(second.Interests);

        second.FailSubscribe = false;
        await hub.Subscribe([new("fake.one", tenant), new("fake.two", tenant)]);
        first.FailUnsubscribe = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => hub.Unsubscribe());
        Assert.Empty(subscriptions.Connections);
        Assert.Empty(second.Interests);
    }

    private sealed class FakeLifecycle(string topic) : IRealtimeTopicLifecycle
    {
        public string Topic => topic;

        public HashSet<string> Interests { get; } = [];

        public bool FailSubscribe { get; set; }

        public bool FailUnsubscribe { get; set; }

        public Task<bool> SubscribeAsync(RealtimeSubscription subscription,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken)
        {
            Interests.Add(subscription.Id);
            if (FailSubscribe)
                throw new InvalidOperationException("owner unavailable");
            return Task.FromResult(true);
        }

        public Task UnsubscribeAsync(RealtimeSubscription subscription, CancellationToken cancellationToken)
        {
            Interests.Remove(subscription.Id);
            if (FailUnsubscribe)
                throw new InvalidOperationException("owner unavailable");
            return Task.CompletedTask;
        }

        public Task<bool> RenewAsync(RealtimeSubscription subscription,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken) =>
            Task.FromResult(Interests.Contains(subscription.Id));
    }

    private sealed class CallerContext : HubCallerContext
    {
        public override string ConnectionId => "connection";
        public override string UserIdentifier => "viewer";
        public override ClaimsPrincipal User => IdentityFixture.User();

        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort()
        {
        }
    }
}
