using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.SignalR;
using Pole.Features.Realtime.Application;
using Pole.Features.Realtime.Contracts;
using Pole.Features.Tenants.Contracts;
using Xunit;

namespace Pole.Features.Realtime.Tests;

public sealed class RealtimePublisherTests
{
    [Fact]
    public async Task PublicEligibilityFanoutDeduplicatesTenantsAndRechecksMembershipWithoutPlatformIdentifiers()
    {
        const string alpha = "000000000000000000000001";
        const string beta = "000000000000000000000002";
        const string expired = "000000000000000000000003";
        var checkedTenants = new List<string>();
        var registry =
            new RealtimeTopicRegistry(
                [new("eligibility", RealtimeScope.Tenant), new("private", RealtimeScope.Platform)],
                new());
        var access = new RealtimeAccess((id, user, _, _) =>
            {
                checkedTenants.Add(id);
                return Task.FromResult<LoadTenantMemberOutcome>(id == alpha
                    ? new TenantMemberSnapshot(id, user, TenantMembershipRole.Viewer)
                    : new TenantMemberMissing(id, user));
            },
            (id, _) => Task.FromResult<LoadTenantIdentityOutcome>(new TenantIdentitySnapshot(id, "name")),
            registry);
        var expiry = DateTimeOffset.UtcNow.AddMinutes(1);
        var tickets = new MemoryTickets();
        await tickets.StoreAsync(new AuthenticationTicket(IdentityFixture.User(),
            new()
            {
                ExpiresUtc = expiry
            },
            RealtimeFeature.CookieScheme));
        var subscriptions = new MemorySubscriptions();
        await subscriptions.ReplaceAsync("connection",
            [
                new("a", "connection", "viewer", new("eligibility", alpha), expiry),
                new("a-detail", "connection", "viewer", new("eligibility", alpha, "private-profile-id"), expiry),
                new("b", "connection", "viewer", new("eligibility", beta), expiry),
                new("old", "connection", "viewer", new("eligibility", expired), DateTimeOffset.UtcNow.AddSeconds(-1))
            ],
            default);
        var hub = new RecordingHubContext();
        var publisher = new RealtimePublisher(subscriptions, tickets, access, registry, hub, TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.InvalidateTenantsAsync("private"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.InvalidateTenantsAsync("unregistered"));
        await publisher.InvalidateTenantsAsync("eligibility");
        Assert.Equal(new RealtimeChange("eligibility", alpha), Assert.Single(hub.Client.Notices));
        Assert.Equal(new[] { alpha, beta }, checkedTenants.Distinct());
        Assert.Equal(1, hub.Client.Revocations);
    }

    [Fact]
    public async Task UserHintsCannotBroadcastMissingSubjectsOrRevealAnotherSubjectsDiscovery()
    {
        var registry = new RealtimeTopicRegistry([new("user-discovery", RealtimeScope.User)], new());
        var access = new RealtimeAccess((_, _, _, _) => throw new InvalidOperationException("Not a tenant scope."),
            (_, _) => throw new InvalidOperationException("Not a tenant scope."),
            registry);
        var tickets = new MemoryTickets();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(1);
        await tickets.StoreAsync(new AuthenticationTicket(IdentityFixture.User(),
            new()
            {
                ExpiresUtc = expiry
            },
            RealtimeFeature.CookieScheme));
        var subscriptions = new MemorySubscriptions();
        await subscriptions.ReplaceAsync("connection",
            [
                new("own", "connection", "viewer", new("user-discovery", ResourceId: "viewer"), expiry),
                new("corrupt-wildcard", "connection", "viewer", new("user-discovery"), expiry)
            ],
            default);
        var hub = new RecordingHubContext();
        var publisher = new RealtimePublisher(subscriptions, tickets, access, registry, hub, TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(new("user-discovery")));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            publisher.PublishAsync(new("user-discovery", "000000000000000000000001", "viewer")));
        await publisher.PublishAsync(new("user-discovery", ResourceId: "other"));
        Assert.Empty(hub.Client.Notices);
        await publisher.PublishAsync(new("user-discovery", ResourceId: "viewer"));
        Assert.Equal("viewer", Assert.Single(hub.Client.Notices).ResourceId);
        Assert.Equal(0, hub.Client.Revocations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostTicketRevokesWorkspaceOnlyWithAnExplicitLogoutMarker(bool loggedOut)
    {
        const string tenant = "000000000000000000000001";
        var registry = new RealtimeTopicRegistry([new("fake", RealtimeScope.Tenant)], new());
        var access = new RealtimeAccess((id, user, _, _) => Task.FromResult<LoadTenantMemberOutcome>(
                new TenantMemberSnapshot(id, user, TenantMembershipRole.Viewer)),
            (id, _) => Task.FromResult<LoadTenantIdentityOutcome>(new TenantIdentitySnapshot(id, "name")),
            registry);
        var subscriptions = new MemorySubscriptions();
        await subscriptions.ReplaceAsync("connection",
            [new("s", "connection", "viewer", new("fake", tenant), DateTimeOffset.UtcNow.AddMinutes(1))],
            default);
        // Reproduce a publisher that already acquired its recipient snapshot when logout commits.
        if (loggedOut)
            subscriptions.Revoked.Add("viewer");
        var hub = new RecordingHubContext();
        await new RealtimePublisher(subscriptions, new MemoryTickets(), access, registry, hub, TimeProvider.System)
            .PublishAsync(new("fake", tenant, "resource"));
        Assert.Empty(hub.Client.Notices);
        Assert.Equal(loggedOut ? 1 : 0, hub.Client.Revocations);
        Assert.Empty(subscriptions.Connections["connection"]);
    }

    [Fact]
    public async Task NaturalTicketExpiryDropsDeliveryWithoutRevokingWorkspacePermissions()
    {
        const string tenant = "000000000000000000000001";
        var registry = new RealtimeTopicRegistry([new("fake", RealtimeScope.Tenant)], new());
        var access = new RealtimeAccess((id, user, _, _) => Task.FromResult<LoadTenantMemberOutcome>(
                new TenantMemberSnapshot(id, user, TenantMembershipRole.Viewer)),
            (id, _) => Task.FromResult<LoadTenantIdentityOutcome>(new TenantIdentitySnapshot(id, "name")),
            registry);
        var tickets = new MemoryTickets();
        await tickets.StoreAsync(new AuthenticationTicket(IdentityFixture.User(),
            new()
            {
                ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(-1)
            },
            RealtimeFeature.CookieScheme));
        var subscriptions = new MemorySubscriptions();
        await subscriptions.ReplaceAsync("connection",
            [new("s", "connection", "viewer", new("fake", tenant), DateTimeOffset.UtcNow.AddMinutes(1))],
            default);
        var hub = new RecordingHubContext();
        await new RealtimePublisher(subscriptions, tickets, access, registry, hub, TimeProvider.System)
            .PublishAsync(new("fake", tenant, "resource"));
        Assert.Empty(hub.Client.Notices);
        Assert.Equal(0, hub.Client.Revocations);
        Assert.Empty(subscriptions.Connections["connection"]);
    }

    [Fact]
    public async Task RevocationDuringDeliveryFencesEverySubscriptionOnTheConnection()
    {
        const string tenant = "000000000000000000000001";
        var checks = 0;
        var registry = new RealtimeTopicRegistry([new("fake", RealtimeScope.Tenant)], new());
        var access = new RealtimeAccess((id, user, _, _) => Task.FromResult<LoadTenantMemberOutcome>(++checks == 1
                ? new TenantMemberSnapshot(id, user, TenantMembershipRole.Viewer)
                : new TenantMemberMissing(id, user)),
            (id, _) => Task.FromResult<LoadTenantIdentityOutcome>(new TenantIdentitySnapshot(id, "name")),
            registry);
        var expiry = DateTimeOffset.UtcNow.AddMinutes(1);
        var tickets = new MemoryTickets();
        await tickets.StoreAsync(new AuthenticationTicket(IdentityFixture.User(),
            new()
            {
                ExpiresUtc = expiry
            },
            RealtimeFeature.CookieScheme));
        var subscriptions = new MemorySubscriptions();
        await subscriptions.ReplaceAsync("connection",
            [
                new("all", "connection", "viewer", new("fake", tenant), expiry),
                new("resource", "connection", "viewer", new("fake", tenant, "resource"), expiry)
            ],
            default);
        var hub = new RecordingHubContext();
        await new RealtimePublisher(subscriptions, tickets, access, registry, hub, TimeProvider.System)
            .PublishAsync(new("fake", tenant, "resource"));
        Assert.Empty(hub.Client.Notices);
        Assert.Equal(1, hub.Client.Revocations);
    }

    [Fact]
    public async Task MembershipIsCheckedAtDeliveryAndExpiredOrRevokedSessionsReceiveNoData()
    {
        const string tenant = "000000000000000000000001";
        var allowed = true;
        var registry = new RealtimeTopicRegistry([new("fake", RealtimeScope.Tenant)], new());
        var access = new RealtimeAccess((id, user, _, _) => Task.FromResult<LoadTenantMemberOutcome>(allowed
                ? new TenantMemberSnapshot(id, user, TenantMembershipRole.Viewer)
                : new TenantMemberMissing(id, user)),
            (id, _) => Task.FromResult<LoadTenantIdentityOutcome>(new TenantIdentitySnapshot(id, "name")),
            registry);
        var tickets = new MemoryTickets();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(1);
        await tickets.StoreAsync(new AuthenticationTicket(IdentityFixture.User(),
            new()
            {
                ExpiresUtc = expiry
            },
            RealtimeFeature.CookieScheme));
        var subscriptions = new MemorySubscriptions();
        await subscriptions.ReplaceAsync("connection",
            [new("s", "connection", "viewer", new("fake", tenant), expiry)],
            default);
        var hub = new RecordingHubContext();
        var publisher = new RealtimePublisher(subscriptions, tickets, access, registry, hub, TimeProvider.System);
        await publisher.PublishAsync(new("fake", tenant, "resource"));
        Assert.Single(hub.Client.Notices);
        allowed = false;
        await publisher.PublishAsync(new("fake", tenant, "resource"));
        Assert.Single(hub.Client.Notices);
        Assert.Empty(subscriptions.Connections["connection"]);
        Assert.Equal(1, hub.Client.Revocations);

        await subscriptions.ReplaceAsync("connection",
            [new("s", "connection", "viewer", new("fake", tenant), expiry)],
            default);
        allowed = true;
        await tickets.RemoveAsync("viewer");
        await publisher.PublishAsync(new("fake", tenant, "resource"));
        Assert.Single(hub.Client.Notices);
    }

    private sealed class RecordingHubContext : IHubContext<RealtimeHub, IRealtimeClient>
    {
        public RecordingClient Client { get; } = new();

        public IHubClients<IRealtimeClient> Clients => new RecordingClients(Client);
        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class RecordingClient : IRealtimeClient
    {
        public List<RealtimeChange> Notices { get; } = [];

        public int Revocations { get; private set; }

        public Task Changed(RealtimeChange change)
        {
            Notices.Add(change);
            return Task.CompletedTask;
        }

        public Task SessionRevoked()
        {
            Revocations++;
            return Task.CompletedTask;
        }

        public Task AccessRevoked(string? tenantId)
        {
            Revocations++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingClients(RecordingClient client) : IHubClients<IRealtimeClient>
    {
        public IRealtimeClient All => client;
        public IRealtimeClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => client;
        public IRealtimeClient Client(string connectionId) => client;
        public IRealtimeClient Clients(IReadOnlyList<string> connectionIds) => client;
        public IRealtimeClient Group(string groupName) => client;
        public IRealtimeClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => client;
        public IRealtimeClient Groups(IReadOnlyList<string> groupNames) => client;
        public IRealtimeClient User(string userId) => client;
        public IRealtimeClient Users(IReadOnlyList<string> userIds) => client;
    }
}
