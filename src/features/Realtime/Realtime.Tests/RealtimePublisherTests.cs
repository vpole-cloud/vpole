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
        var registry = new RealtimeTopicRegistry(
            [
                new RealtimeTopic("eligibility", RealtimeScope.Tenant),
                new RealtimeTopic("private", RealtimeScope.Platform),
            ],
            new RealtimeOptions());
        var access = new RealtimeAccess(
            (
                request, _,
                _) =>
            {
                checkedTenants.Add(request.TenantId);
                return Task.FromResult<LoadTenantMemberOutcome>(
                    request.TenantId == alpha
                        ? new TenantMemberSnapshot(request.TenantId, request.UserId, TenantMembershipRole.Viewer)
                        : new TenantMemberMissing(request.TenantId, request.UserId));
            },
            (id, _) => Task.FromResult<LoadTenantIdentityOutcome>(new TenantIdentitySnapshot(id, "name")),
            registry);
        var expiry = DateTimeOffset.UtcNow.AddMinutes(1);
        var tickets = new MemoryTickets();
        await tickets.StoreAsync(
            new AuthenticationTicket(
                IdentityFixture.User(),
                new AuthenticationProperties
                {
                    ExpiresUtc = expiry,
                },
                RealtimeFeature.CookieScheme));
        var subscriptions = new MemorySubscriptions();
        await subscriptions.ReplaceAsync(
            "connection",
            [
                new RealtimeSubscription(
                    "a",
                    "connection",
                    "viewer",
                    new RealtimeSubscriptionRequest("eligibility", alpha),
                    expiry),
                new RealtimeSubscription(
                    "a-detail",
                    "connection",
                    "viewer",
                    new RealtimeSubscriptionRequest("eligibility", alpha, "private-profile-id"),
                    expiry),
                new RealtimeSubscription(
                    "b",
                    "connection",
                    "viewer",
                    new RealtimeSubscriptionRequest("eligibility", beta),
                    expiry),
                new RealtimeSubscription(
                    "old",
                    "connection",
                    "viewer",
                    new RealtimeSubscriptionRequest("eligibility", expired),
                    DateTimeOffset.UtcNow.AddSeconds(-1)),
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
        var registry = new RealtimeTopicRegistry(
            [new RealtimeTopic("user-discovery", RealtimeScope.User)],
            new RealtimeOptions());
        var access = new RealtimeAccess(
            (
                request, _,
                _) => throw new InvalidOperationException("Not a tenant scope."),
            (_, _) => throw new InvalidOperationException("Not a tenant scope."),
            registry);
        var tickets = new MemoryTickets();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(1);
        await tickets.StoreAsync(
            new AuthenticationTicket(
                IdentityFixture.User(),
                new AuthenticationProperties
                {
                    ExpiresUtc = expiry,
                },
                RealtimeFeature.CookieScheme));
        var subscriptions = new MemorySubscriptions();
        await subscriptions.ReplaceAsync(
            "connection",
            [
                new RealtimeSubscription(
                    "own",
                    "connection",
                    "viewer",
                    new RealtimeSubscriptionRequest("user-discovery", ResourceId: "viewer"),
                    expiry),
                new RealtimeSubscription(
                    "corrupt-wildcard",
                    "connection",
                    "viewer",
                    new RealtimeSubscriptionRequest("user-discovery"),
                    expiry),
            ],
            default);
        var hub = new RecordingHubContext();
        var publisher = new RealtimePublisher(subscriptions, tickets, access, registry, hub, TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            publisher.PublishAsync(new RealtimeChange("user-discovery")));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            publisher.PublishAsync(new RealtimeChange("user-discovery", "000000000000000000000001", "viewer")));
        await publisher.PublishAsync(new RealtimeChange("user-discovery", ResourceId: "other"));
        Assert.Empty(hub.Client.Notices);
        await publisher.PublishAsync(new RealtimeChange("user-discovery", ResourceId: "viewer"));
        Assert.Equal("viewer", Assert.Single(hub.Client.Notices).ResourceId);
        Assert.Equal(0, hub.Client.Revocations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostTicketRevokesWorkspaceOnlyWithAnExplicitLogoutMarker(bool loggedOut)
    {
        const string tenant = "000000000000000000000001";
        var registry = new RealtimeTopicRegistry(
            [new RealtimeTopic("fake", RealtimeScope.Tenant)],
            new RealtimeOptions());
        var access = new RealtimeAccess(
            (
                request, _,
                _) => Task.FromResult<LoadTenantMemberOutcome>(
                new TenantMemberSnapshot(request.TenantId, request.UserId, TenantMembershipRole.Viewer)),
            (id, _) => Task.FromResult<LoadTenantIdentityOutcome>(new TenantIdentitySnapshot(id, "name")),
            registry);
        var subscriptions = new MemorySubscriptions();
        await subscriptions.ReplaceAsync(
            "connection",
            [
                new RealtimeSubscription(
                    "s",
                    "connection",
                    "viewer",
                    new RealtimeSubscriptionRequest("fake", tenant),
                    DateTimeOffset.UtcNow.AddMinutes(1)),
            ],
            default);
        // Reproduce a publisher that already acquired its recipient snapshot when logout commits.
        if (loggedOut)
            subscriptions.Revoked.Add("viewer");
        var hub = new RecordingHubContext();
        await new RealtimePublisher(subscriptions, new MemoryTickets(), access, registry, hub, TimeProvider.System)
            .PublishAsync(new RealtimeChange("fake", tenant, "resource"));
        Assert.Empty(hub.Client.Notices);
        Assert.Equal(loggedOut ? 1 : 0, hub.Client.Revocations);
        Assert.Empty(subscriptions.Connections["connection"]);
    }

    [Fact]
    public async Task NaturalTicketExpiryDropsDeliveryWithoutRevokingWorkspacePermissions()
    {
        const string tenant = "000000000000000000000001";
        var registry = new RealtimeTopicRegistry(
            [new RealtimeTopic("fake", RealtimeScope.Tenant)],
            new RealtimeOptions());
        var access = new RealtimeAccess(
            (
                request, _,
                _) => Task.FromResult<LoadTenantMemberOutcome>(
                new TenantMemberSnapshot(request.TenantId, request.UserId, TenantMembershipRole.Viewer)),
            (id, _) => Task.FromResult<LoadTenantIdentityOutcome>(new TenantIdentitySnapshot(id, "name")),
            registry);
        var tickets = new MemoryTickets();
        await tickets.StoreAsync(
            new AuthenticationTicket(
                IdentityFixture.User(),
                new AuthenticationProperties
                {
                    ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
                },
                RealtimeFeature.CookieScheme));
        var subscriptions = new MemorySubscriptions();
        await subscriptions.ReplaceAsync(
            "connection",
            [
                new RealtimeSubscription(
                    "s",
                    "connection",
                    "viewer",
                    new RealtimeSubscriptionRequest("fake", tenant),
                    DateTimeOffset.UtcNow.AddMinutes(1)),
            ],
            default);
        var hub = new RecordingHubContext();
        await new RealtimePublisher(subscriptions, tickets, access, registry, hub, TimeProvider.System).PublishAsync(
            new RealtimeChange("fake", tenant, "resource"));
        Assert.Empty(hub.Client.Notices);
        Assert.Equal(0, hub.Client.Revocations);
        Assert.Empty(subscriptions.Connections["connection"]);
    }

    [Fact]
    public async Task RevocationDuringDeliveryFencesEverySubscriptionOnTheConnection()
    {
        const string tenant = "000000000000000000000001";
        var checks = 0;
        var registry = new RealtimeTopicRegistry(
            [new RealtimeTopic("fake", RealtimeScope.Tenant)],
            new RealtimeOptions());
        var access = new RealtimeAccess(
            (
                request, _,
                _) => Task.FromResult<LoadTenantMemberOutcome>(
                ++checks == 1
                    ? new TenantMemberSnapshot(request.TenantId, request.UserId, TenantMembershipRole.Viewer)
                    : new TenantMemberMissing(request.TenantId, request.UserId)),
            (id, _) => Task.FromResult<LoadTenantIdentityOutcome>(new TenantIdentitySnapshot(id, "name")),
            registry);
        var expiry = DateTimeOffset.UtcNow.AddMinutes(1);
        var tickets = new MemoryTickets();
        await tickets.StoreAsync(
            new AuthenticationTicket(
                IdentityFixture.User(),
                new AuthenticationProperties
                {
                    ExpiresUtc = expiry,
                },
                RealtimeFeature.CookieScheme));
        var subscriptions = new MemorySubscriptions();
        await subscriptions.ReplaceAsync(
            "connection",
            [
                new RealtimeSubscription(
                    "all",
                    "connection",
                    "viewer",
                    new RealtimeSubscriptionRequest("fake", tenant),
                    expiry),
                new RealtimeSubscription(
                    "resource",
                    "connection",
                    "viewer",
                    new RealtimeSubscriptionRequest("fake", tenant, "resource"),
                    expiry),
            ],
            default);
        var hub = new RecordingHubContext();
        await new RealtimePublisher(subscriptions, tickets, access, registry, hub, TimeProvider.System).PublishAsync(
            new RealtimeChange("fake", tenant, "resource"));
        Assert.Empty(hub.Client.Notices);
        Assert.Equal(1, hub.Client.Revocations);
    }

    [Fact]
    public async Task MembershipIsCheckedAtDeliveryAndExpiredOrRevokedSessionsReceiveNoData()
    {
        const string tenant = "000000000000000000000001";
        var allowed = true;
        var registry = new RealtimeTopicRegistry(
            [new RealtimeTopic("fake", RealtimeScope.Tenant)],
            new RealtimeOptions());
        var access = new RealtimeAccess(
            (
                request, _,
                _) => Task.FromResult<LoadTenantMemberOutcome>(
                allowed
                    ? new TenantMemberSnapshot(request.TenantId, request.UserId, TenantMembershipRole.Viewer)
                    : new TenantMemberMissing(request.TenantId, request.UserId)),
            (id, _) => Task.FromResult<LoadTenantIdentityOutcome>(new TenantIdentitySnapshot(id, "name")),
            registry);
        var tickets = new MemoryTickets();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(1);
        await tickets.StoreAsync(
            new AuthenticationTicket(
                IdentityFixture.User(),
                new AuthenticationProperties
                {
                    ExpiresUtc = expiry,
                },
                RealtimeFeature.CookieScheme));
        var subscriptions = new MemorySubscriptions();
        await subscriptions.ReplaceAsync(
            "connection",
            [
                new RealtimeSubscription(
                    "s",
                    "connection",
                    "viewer",
                    new RealtimeSubscriptionRequest("fake", tenant),
                    expiry),
            ],
            default);
        var hub = new RecordingHubContext();
        var publisher = new RealtimePublisher(subscriptions, tickets, access, registry, hub, TimeProvider.System);
        await publisher.PublishAsync(new RealtimeChange("fake", tenant, "resource"));
        Assert.Single(hub.Client.Notices);
        allowed = false;
        await publisher.PublishAsync(new RealtimeChange("fake", tenant, "resource"));
        Assert.Single(hub.Client.Notices);
        Assert.Empty(subscriptions.Connections["connection"]);
        Assert.Equal(1, hub.Client.Revocations);

        await subscriptions.ReplaceAsync(
            "connection",
            [
                new RealtimeSubscription(
                    "s",
                    "connection",
                    "viewer",
                    new RealtimeSubscriptionRequest("fake", tenant),
                    expiry),
            ],
            default);
        allowed = true;
        await tickets.RemoveAsync("viewer");
        await publisher.PublishAsync(new RealtimeChange("fake", tenant, "resource"));
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

    private sealed class RecordingClients(
        RecordingClient client
    ) : IHubClients<IRealtimeClient>
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
