using Pole.Features.Realtime.Application;
using Pole.Features.Realtime.Contracts;
using Pole.Features.Tenants.Contracts;
using Xunit;

namespace Pole.Features.Realtime.Tests;

public sealed class RealtimeAccessTests
{
    private const string Alpha = "000000000000000000000001";
    private const string Beta = "000000000000000000000002";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserTopicsRequireExactlyTheAuthenticatedSubjectEvenForPlatformAdmins(bool admin)
    {
        var registry = new RealtimeTopicRegistry(
            [new RealtimeTopic("user-discovery", RealtimeScope.User)],
            new RealtimeOptions());
        var access = new RealtimeAccess(
            (
                request, _,
                _) => throw new InvalidOperationException("User discovery must not require tenant membership."),
            (_, _) => throw new InvalidOperationException("User discovery must not load a tenant."),
            registry);
        var principal = IdentityFixture.User(admin: admin);
        Assert.True(
            await access.AllowsAsync(
                principal,
                new RealtimeSubscriptionRequest("user-discovery", ResourceId: "viewer"),
                default));
        Assert.False(
            await access.AllowsAsync(
                principal,
                new RealtimeSubscriptionRequest("user-discovery", ResourceId: "other"),
                default));
        Assert.False(await access.AllowsAsync(principal, new RealtimeSubscriptionRequest("user-discovery"), default));
        Assert.False(
            await access.AllowsAsync(
                principal,
                new RealtimeSubscriptionRequest("user-discovery", Alpha, "viewer"),
                default));
    }

    [Fact]
    public async Task FakeProviderExtendsTopicsAndPreservesTenantIsolation()
    {
        var registry = new RealtimeTopicRegistry(
            [new RealtimeTopic("plugin.example", RealtimeScope.Tenant)],
            new RealtimeOptions());
        var access = new RealtimeAccess(
            (
                request, _,
                _) => Task.FromResult<LoadTenantMemberOutcome>(
                request.TenantId == Alpha && request.UserId == "viewer"
                    ? new TenantMemberSnapshot(request.TenantId, request.UserId, TenantMembershipRole.Viewer)
                    : new TenantMemberMissing(request.TenantId, request.UserId)),
            (tenant, _) => Task.FromResult<LoadTenantIdentityOutcome>(new TenantIdentitySnapshot(tenant, "name")),
            registry);
        Assert.True(
            await access.AllowsAsync(
                IdentityFixture.User(),
                new RealtimeSubscriptionRequest("plugin.example", Alpha),
                default));
        Assert.False(
            await access.AllowsAsync(
                IdentityFixture.User(),
                new RealtimeSubscriptionRequest("plugin.example", Beta),
                default));
        Assert.False(
            await access.AllowsAsync(
                IdentityFixture.User(),
                new RealtimeSubscriptionRequest("plugin.example", "invalid"),
                default));
        Assert.False(
            await access.AllowsAsync(
                IdentityFixture.User(),
                new RealtimeSubscriptionRequest("not-registered", Alpha),
                default));
    }

    [Fact]
    public async Task PlatformAdminStillNeedsAnExistingTenantAndPlatformScopeIsExplicit()
    {
        var registry = new RealtimeTopicRegistry(
            [
                new RealtimeTopic("catalog", RealtimeScope.Platform),
                new RealtimeTopic("resources", RealtimeScope.Tenant),
            ],
            new RealtimeOptions());
        var access = new RealtimeAccess(
            (
                request, _,
                _) => Task.FromResult<LoadTenantMemberOutcome>(
                new TenantMemberMissing(request.TenantId, request.UserId)),
            (tenant, _) => Task.FromResult<LoadTenantIdentityOutcome>(
                tenant == Alpha ? new TenantIdentitySnapshot(tenant, "name") : new TenantIdentityNotFound(tenant)),
            registry);
        Assert.True(
            await access.AllowsAsync(
                IdentityFixture.User(admin: true),
                new RealtimeSubscriptionRequest("catalog"),
                default));
        Assert.False(
            await access.AllowsAsync(IdentityFixture.User(), new RealtimeSubscriptionRequest("catalog"), default));
        Assert.False(
            await access.AllowsAsync(
                IdentityFixture.User(admin: true),
                new RealtimeSubscriptionRequest("catalog", Alpha),
                default));
        Assert.True(
            await access.AllowsAsync(
                IdentityFixture.User(admin: true),
                new RealtimeSubscriptionRequest("resources", Alpha),
                default));
        Assert.False(
            await access.AllowsAsync(
                IdentityFixture.User(admin: true),
                new RealtimeSubscriptionRequest("resources", Beta),
                default));
    }

    [Fact]
    public void DuplicateContributionsAndUnknownEnabledTopicsFailStartup()
    {
        Assert.Throws<InvalidOperationException>(() => new RealtimeTopicRegistry(
            [new RealtimeTopic("same", RealtimeScope.Tenant), new RealtimeTopic("same", RealtimeScope.Tenant)],
            new RealtimeOptions()));
        Assert.Throws<InvalidOperationException>(() => new RealtimeTopicRegistry(
            [],
            new RealtimeOptions
            {
                Enabled = true,
                EnabledTopics = ["unknown"],
            }));
    }

    [Fact]
    public async Task AvailabilityDoesNotChangePermissionsAndDisabledStartupIgnoresStaleProviderConfiguration()
    {
        var registry = new RealtimeTopicRegistry(
            [new RealtimeTopic("enabled", RealtimeScope.Tenant), new RealtimeTopic("disabled", RealtimeScope.Tenant)],
            new RealtimeOptions
            {
                Enabled = true,
                EnabledTopics = ["enabled"],
            });
        var access = new RealtimeAccess(
            (
                request, _,
                _) => Task.FromResult<LoadTenantMemberOutcome>(
                request.TenantId == Alpha
                    ? new TenantMemberSnapshot(request.TenantId, request.UserId, TenantMembershipRole.Viewer)
                    : new TenantMemberMissing(request.TenantId, request.UserId)),
            (tenant, _) => Task.FromResult<LoadTenantIdentityOutcome>(new TenantIdentityNotFound(tenant)),
            registry);
        Assert.True(
            await access.AllowsAsync(
                IdentityFixture.User(),
                new RealtimeSubscriptionRequest("disabled", Alpha),
                default));
        Assert.False(
            await access.AllowsAsync(
                IdentityFixture.User(),
                new RealtimeSubscriptionRequest("disabled", Beta),
                default));
        Assert.False(access.IsAvailable("disabled"));
        Assert.False(access.IsAvailable("unknown"));
        Assert.True(access.IsAvailable("enabled"));
        Assert.Null(
            new RealtimeTopicRegistry(
                [],
                new RealtimeOptions
                {
                    Enabled = false,
                    EnabledTopics = ["removed-provider"],
                }).Find("removed-provider"));
        Assert.Throws<InvalidOperationException>(() => new RealtimeTopicRegistry(
            [new RealtimeTopic("same", RealtimeScope.Tenant), new RealtimeTopic("same", RealtimeScope.Tenant)],
            new RealtimeOptions
            {
                Enabled = false,
                EnabledTopics = ["removed-provider"],
            }));
    }
}
