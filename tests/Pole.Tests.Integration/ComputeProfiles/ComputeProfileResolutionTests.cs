using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Application.Catalog;
using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.ComputeProfiles.Migrations;
using Pole.Features.DeploymentTargets.Migrations;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ComputeProfiles;

[Collection(IntegrationTestCollection.Global)]
public sealed class ComputeProfileResolutionTests(
    MongoReplicaSetFixture fixture
) : MongoIntegrationTestBase(fixture)
{
    private static readonly ResourceTypeId WebServiceType = ResourceTypeId.Create("pole/web-service");

    public override Task InitializeAsync() => base.InitializeAsync();

    public override Task DisposeAsync() => base.DisposeAsync();

    [Fact]
    public async Task Resolve_default_returns_exact_target_snapshot_and_enforces_availability()
    {
        await PrepareDatabaseAsync();
        var target = ComputeProfileTestData.CreateTarget();
        var otherTarget = ComputeProfileTestData.CreateTarget();
        await Fixture.DbContext.DeploymentTargets.InsertManyAsync([target, otherTarget]);
        using var host = new ComputeProfilePersistenceTestHost(Fixture);
        var profileOutcome = await host.GetRequiredService<PersistCreatedComputeProfile>()(
            ComputeProfileTestData.CreateProfile(
                target.Id,
                isDefault: true,
                tenantAccess: SelectedTenantsComputeProfileAccess.Create([ComputeProfileTestData.TenantId])));
        var profile = Assert.IsType<ComputeProfile>(profileOutcome.Value);
        var resolve = host.GetRequiredService<ResolveComputeProfile>();

        var resolvedOutcome = await resolve(
            new ComputeProfileResolutionRequest(
                ComputeProfileId: null,
                target.Id,
                ComputeProfileTestData.TenantId,
                WebServiceType));
        var resolved = Assert.IsType<ResolvedComputeProfile>(resolvedOutcome.Value);

        Assert.Equal(profile.Id, resolved.ComputeProfileId);
        Assert.Equal(profile.Revision, resolved.ComputeProfileRevision);
        Assert.Equal(target.Id, resolved.DeploymentTargetId);
        Assert.Equal(profile.Resources.Requests.CpuMillis, resolved.Requests.CpuMillis);
        Assert.Equal(profile.ExtendedResources.Single().Name, resolved.ExtendedResources.Single().Name);
        Assert.Equal(profile.TargetMappings.Single().RuntimeClassName, resolved.RuntimeClassName);

        var wrongTenant = await resolve(
            new ComputeProfileResolutionRequest(
                profile.Id,
                target.Id,
                ComputeProfileTestData.OtherTenantId,
                WebServiceType));
        var wrongTenantError = Assert.IsType<ComputeProfileResolutionError>(wrongTenant.Value);
        Assert.IsType<ComputeProfileUnavailable>(wrongTenantError.Value);

        var wrongResourceType = await resolve(
            new ComputeProfileResolutionRequest(
                profile.Id,
                target.Id,
                ComputeProfileTestData.TenantId,
                ResourceTypeId.Create("third-party/widget")));
        var wrongResourceTypeError = Assert.IsType<ComputeProfileResolutionError>(wrongResourceType.Value);
        Assert.IsType<ComputeProfileUnavailable>(wrongResourceTypeError.Value);

        var missingMapping = await resolve(
            new ComputeProfileResolutionRequest(
                profile.Id,
                otherTarget.Id,
                ComputeProfileTestData.TenantId,
                WebServiceType));
        var missingMappingError = Assert.IsType<ComputeProfileResolutionError>(missingMapping.Value);
        Assert.IsType<ComputeProfileUnavailable>(missingMappingError.Value);
    }

    [Fact]
    public async Task Resolve_many_resolves_default_and_explicit_profiles_for_each_target()
    {
        await PrepareDatabaseAsync();
        var firstTarget = ComputeProfileTestData.CreateTarget();
        var secondTarget = ComputeProfileTestData.CreateTarget();
        await Fixture.DbContext.DeploymentTargets.InsertManyAsync([firstTarget, secondTarget]);
        using var host = new ComputeProfilePersistenceTestHost(Fixture);
        var create = host.GetRequiredService<PersistCreatedComputeProfile>();
        var defaultProfileOutcome = await create(ComputeProfileTestData.CreateProfile(firstTarget.Id, isDefault: true));
        var defaultProfile = Assert.IsType<ComputeProfile>(defaultProfileOutcome.Value);
        var explicitProfileOutcome = await create(
            ComputeProfileTestData.CreateProfile(secondTarget.Id, displayName: "Target specific"));
        var explicitProfile = Assert.IsType<ComputeProfile>(explicitProfileOutcome.Value);
        var resolveMany = host.GetRequiredService<ResolveComputeProfiles>();

        var resolvedOutcome = await resolveMany(
            new ComputeProfileBatchResolutionRequest(
                ComputeProfileTestData.TenantId,
                WebServiceType,
                [
                    new ComputeProfileTargetResolutionRequest(firstTarget.Id, null),
                    new ComputeProfileTargetResolutionRequest(secondTarget.Id, explicitProfile.Id),
                ]));
        var resolved = Assert.IsAssignableFrom<IReadOnlyList<ResolvedComputeProfile>>(resolvedOutcome.Value);

        Assert.Collection(
            resolved,
            profile =>
            {
                Assert.Equal(defaultProfile.Id, profile.ComputeProfileId);
                Assert.Equal(firstTarget.Id, profile.DeploymentTargetId);
            },
            profile =>
            {
                Assert.Equal(explicitProfile.Id, profile.ComputeProfileId);
                Assert.Equal(secondTarget.Id, profile.DeploymentTargetId);
            });
    }

    [Fact]
    public async Task Fence_advances_coordination_state_and_rejects_stale_profile_revision()
    {
        await PrepareDatabaseAsync();
        var target = ComputeProfileTestData.CreateTarget();
        await Fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        using var host = new ComputeProfilePersistenceTestHost(Fixture);
        var create = host.GetRequiredService<PersistCreatedComputeProfile>();
        var update = host.GetRequiredService<PersistUpdatedComputeProfile>();
        var resolve = host.GetRequiredService<ResolveComputeProfile>();
        var fence = host.GetRequiredService<FenceResolvedComputeProfiles>();
        var originalOutcome = await create(
            ComputeProfileTestData.CreateProfile(
                target.Id,
                tenantAccess: SelectedTenantsComputeProfileAccess.Create([ComputeProfileTestData.TenantId])));
        var original = Assert.IsType<ComputeProfile>(originalOutcome.Value);
        var resolvedOutcome = await resolve(
            new ComputeProfileResolutionRequest(
                original.Id,
                target.Id,
                ComputeProfileTestData.TenantId,
                WebServiceType));
        var resolved = Assert.IsType<ResolvedComputeProfile>(resolvedOutcome.Value);

        var fenced = await FenceInTransactionAsync(
            fence,
            [
                new ComputeProfileResolutionFenceRequest(
                    resolved.ComputeProfileId,
                    resolved.ComputeProfileRevision,
                    resolved.DeploymentTargetId,
                    ComputeProfileTestData.TenantId,
                    WebServiceType),
            ]);
        Assert.IsType<ComputeProfilesFenced>(fenced.Value);

        var afterFence = await LoadAsync(original.Id);
        Assert.Equal(1, afterFence.ResolutionFence);

        var staleReplacement = Reconfigure(original, "Stale update");
        var staleUpdate = await update(original, staleReplacement);
        Assert.IsType<ComputeProfileRevisionConflict>(staleUpdate.Value);

        var updatedOutcome = await update(afterFence, Reconfigure(afterFence, "Current update"));
        var updated = Assert.IsType<ComputeProfile>(updatedOutcome.Value);
        Assert.Equal(2, updated.Revision);
        Assert.Equal(1, updated.ResolutionFence);

        var staleFence = await FenceInTransactionAsync(
            fence,
            [
                new ComputeProfileResolutionFenceRequest(
                    resolved.ComputeProfileId,
                    resolved.ComputeProfileRevision,
                    resolved.DeploymentTargetId,
                    ComputeProfileTestData.TenantId,
                    WebServiceType),
            ]);
        Assert.IsType<ComputeProfileResolutionFenceRejected>(staleFence.Value);
    }

    [Fact]
    public async Task Fence_many_is_atomic_when_one_profile_snapshot_is_stale()
    {
        await PrepareDatabaseAsync();
        var firstTarget = ComputeProfileTestData.CreateTarget();
        var secondTarget = ComputeProfileTestData.CreateTarget();
        await Fixture.DbContext.DeploymentTargets.InsertManyAsync([firstTarget, secondTarget]);
        using var host = new ComputeProfilePersistenceTestHost(Fixture);
        var create = host.GetRequiredService<PersistCreatedComputeProfile>();
        var update = host.GetRequiredService<PersistUpdatedComputeProfile>();
        var resolveMany = host.GetRequiredService<ResolveComputeProfiles>();
        var fence = host.GetRequiredService<FenceResolvedComputeProfiles>();
        var firstOutcome = await create(ComputeProfileTestData.CreateProfile(firstTarget.Id));
        var first = Assert.IsType<ComputeProfile>(firstOutcome.Value);
        var secondOutcome = await create(ComputeProfileTestData.CreateProfile(secondTarget.Id, displayName: "Second"));
        var second = Assert.IsType<ComputeProfile>(secondOutcome.Value);
        var resolvedOutcome = await resolveMany(
            new ComputeProfileBatchResolutionRequest(
                ComputeProfileTestData.TenantId,
                WebServiceType,
                [
                    new ComputeProfileTargetResolutionRequest(firstTarget.Id, first.Id),
                    new ComputeProfileTargetResolutionRequest(secondTarget.Id, second.Id),
                ]));
        var resolved = Assert.IsAssignableFrom<IReadOnlyList<ResolvedComputeProfile>>(resolvedOutcome.Value);

        var fenceRequests = resolved.Select(profile => new ComputeProfileResolutionFenceRequest(
                profile.ComputeProfileId,
                profile.ComputeProfileRevision,
                profile.DeploymentTargetId,
                ComputeProfileTestData.TenantId,
                WebServiceType))
            .ToArray();
        Assert.IsType<ComputeProfilesFenced>((await FenceInTransactionAsync(fence, fenceRequests)).Value);
        var firstAfterFence = await LoadAsync(first.Id);
        var secondAfterFence = await LoadAsync(second.Id);
        Assert.Equal(1, firstAfterFence.ResolutionFence);
        Assert.Equal(1, secondAfterFence.ResolutionFence);
        _ = Assert.IsType<ComputeProfile>(
            (await update(firstAfterFence, Reconfigure(firstAfterFence, "Changed before batch fence"))).Value);

        var fenced = await FenceInTransactionAsync(fence, fenceRequests);

        Assert.IsType<ComputeProfileResolutionFenceRejected>(fenced.Value);
        Assert.Equal(1, (await LoadAsync(first.Id)).ResolutionFence);
        Assert.Equal(1, (await LoadAsync(second.Id)).ResolutionFence);
    }

    [Fact]
    public async Task Availability_query_filters_tenant_resource_and_target_without_parallel_array_index()
    {
        await PrepareDatabaseAsync();
        var target = ComputeProfileTestData.CreateTarget();
        var otherTarget = ComputeProfileTestData.CreateTarget();
        await Fixture.DbContext.DeploymentTargets.InsertManyAsync([target, otherTarget]);
        using var host = new ComputeProfilePersistenceTestHost(Fixture);
        var create = host.GetRequiredService<PersistCreatedComputeProfile>();
        var availableProfileOutcome = await create(
            ComputeProfileTestData.CreateProfile(
                target.Id,
                tenantAccess: SelectedTenantsComputeProfileAccess.Create([ComputeProfileTestData.TenantId]),
                displayName: "Available"));
        var availableProfile = Assert.IsType<ComputeProfile>(availableProfileOutcome.Value);
        await create(
            ComputeProfileTestData.CreateProfile(
                target.Id,
                tenantAccess: SelectedTenantsComputeProfileAccess.Create([ComputeProfileTestData.OtherTenantId]),
                displayName: "Other tenant"));
        await create(
            ComputeProfileTestData.CreateProfile(
                target.Id,
                resourceTypes: [ResourceTypeId.Create("third-party/widget")],
                displayName: "Other resource"));
        await create(ComputeProfileTestData.CreateProfile(otherTarget.Id, displayName: "Other target"));

        var profiles = await host.GetRequiredService<LoadAvailableComputeProfiles>()(
            new ComputeProfileAvailabilityQuery(ComputeProfileTestData.TenantId, target.Id, WebServiceType));

        Assert.Equal([availableProfile.Id], profiles.Select(profile => profile.Id));
    }

    private async Task PrepareDatabaseAsync()
    {
        await new DeploymentTargetReferenceMigration().Up(Fixture.Database);
        await new ComputeProfileSchemaValidationMigration().Up(Fixture.Database);
        await new ComputeProfileIndexesMigration().Up(Fixture.Database);
    }

    private async Task<ComputeProfile> LoadAsync(string id) => await Fixture.Database
        .GetCollection<ComputeProfile>("ComputeProfiles")
        .Find(ComputeProfileById(id))
        .SingleAsync();

    private static FilterDefinition<ComputeProfile> ComputeProfileById(string computeProfileId) =>
        Builders<ComputeProfile>.Filter.Eq(profile => profile.Id, computeProfileId);

    private async Task<FenceResolvedComputeProfilesOutcome> FenceInTransactionAsync(
        FenceResolvedComputeProfiles fence,
        IReadOnlyList<ComputeProfileResolutionFenceRequest> requests)
    {
        using var session = await Fixture.DbContext.Client.StartSessionAsync();
        session.StartTransaction();
        try
        {
            var result = await fence(requests, session);
            if (result is ComputeProfilesFenced)
                await session.CommitTransactionAsync();
            else
                await session.AbortTransactionAsync();
            return result;
        }
        catch
        {
            if (session.IsInTransaction)
                await session.AbortTransactionAsync();
            throw;
        }
    }

    private static ComputeProfile Reconfigure(ComputeProfile current, string description) => current.Reconfigure(
        current.DisplayName,
        description,
        current.Resources,
        current.Architecture,
        current.ExtendedResources,
        current.AllowedResourceTypes.Select(ResourceTypeId.Create),
        current.TenantAccess,
        current.TargetMappings,
        current.IsEnabled,
        current.IsDefault,
        current.UpdatedAtUtc.AddMinutes(1));
}
