using Pole.Features.Realtime.Application;
using Pole.Features.Realtime.Contracts;
using Pole.Features.Tenants.Contracts;
using Xunit;

namespace Pole.Features.Realtime.Tests;

public sealed class RealtimeAccessTests
{
    private const string Alpha = "000000000000000000000001";
    private const string Beta = "000000000000000000000002";

    [Fact]
    public async Task FakeProviderExtendsTopicsAndPreservesTenantIsolation()
    {
        var registry = new RealtimeTopicRegistry([new("plugin.example", RealtimeScope.Tenant)], new());
        var access = new RealtimeAccess((tenant, user, _, _) => Task.FromResult<LoadTenantMemberOutcome>(
            tenant == Alpha && user == "viewer" ? new TenantMemberSnapshot(tenant, user, TenantMembershipRole.Viewer) : new TenantMemberMissing(tenant, user)),
            (tenant, _) => Task.FromResult<LoadTenantIdentityOutcome>(new TenantIdentitySnapshot(tenant, "name")), registry);
        Assert.True(await access.AllowsAsync(IdentityFixture.User(), new("plugin.example", Alpha), default));
        Assert.False(await access.AllowsAsync(IdentityFixture.User(), new("plugin.example", Beta), default));
        Assert.False(await access.AllowsAsync(IdentityFixture.User(), new("plugin.example", "invalid"), default));
        Assert.False(await access.AllowsAsync(IdentityFixture.User(), new("not-registered", Alpha), default));
    }

    [Fact]
    public async Task PlatformAdminStillNeedsAnExistingTenantAndPlatformScopeIsExplicit()
    {
        var registry = new RealtimeTopicRegistry([new("catalog", RealtimeScope.Platform), new("resources", RealtimeScope.Tenant)], new());
        var access = new RealtimeAccess((tenant, user, _, _) => Task.FromResult<LoadTenantMemberOutcome>(new TenantMemberMissing(tenant, user)),
            (tenant, _) => Task.FromResult<LoadTenantIdentityOutcome>(tenant == Alpha ? new TenantIdentitySnapshot(tenant, "name") : new TenantIdentityNotFound(tenant)), registry);
        Assert.True(await access.AllowsAsync(IdentityFixture.User(admin: true), new("catalog"), default));
        Assert.False(await access.AllowsAsync(IdentityFixture.User(), new("catalog"), default));
        Assert.False(await access.AllowsAsync(IdentityFixture.User(admin: true), new("catalog", Alpha), default));
        Assert.True(await access.AllowsAsync(IdentityFixture.User(admin: true), new("resources", Alpha), default));
        Assert.False(await access.AllowsAsync(IdentityFixture.User(admin: true), new("resources", Beta), default));
    }

    [Fact]
    public void DuplicateContributionsAndUnknownEnabledTopicsFailStartup()
    {
        Assert.Throws<InvalidOperationException>(() => new RealtimeTopicRegistry([new("same", RealtimeScope.Tenant), new("same", RealtimeScope.Tenant)], new()));
        Assert.Throws<InvalidOperationException>(() => new RealtimeTopicRegistry([], new() { Enabled = true, EnabledTopics = ["unknown"] }));
    }

    [Fact]
    public async Task AvailabilityDoesNotChangePermissionsAndDisabledStartupIgnoresStaleProviderConfiguration()
    {
        var registry = new RealtimeTopicRegistry([new("enabled", RealtimeScope.Tenant), new("disabled", RealtimeScope.Tenant)],
            new() { Enabled = true, EnabledTopics = ["enabled"] });
        var access = new RealtimeAccess((tenant, user, _, _) => Task.FromResult<LoadTenantMemberOutcome>(
            tenant == Alpha ? new TenantMemberSnapshot(tenant, user, TenantMembershipRole.Viewer) : new TenantMemberMissing(tenant, user)),
            (tenant, _) => Task.FromResult<LoadTenantIdentityOutcome>(new TenantIdentityNotFound(tenant)), registry);
        Assert.True(await access.AllowsAsync(IdentityFixture.User(), new("disabled", Alpha), default));
        Assert.False(await access.AllowsAsync(IdentityFixture.User(), new("disabled", Beta), default));
        Assert.False(access.IsAvailable("disabled"));
        Assert.False(access.IsAvailable("unknown"));
        Assert.True(access.IsAvailable("enabled"));
        Assert.Null(new RealtimeTopicRegistry([], new() { Enabled = false, EnabledTopics = ["removed-provider"] }).Find("removed-provider"));
        Assert.Throws<InvalidOperationException>(() => new RealtimeTopicRegistry(
            [new("same", RealtimeScope.Tenant), new("same", RealtimeScope.Tenant)],
            new() { Enabled = false, EnabledTopics = ["removed-provider"] }));
    }
}
