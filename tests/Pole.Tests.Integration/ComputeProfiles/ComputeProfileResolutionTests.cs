using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Application.Migrations;
using Pole.Features.ComputeProfiles.Application.Persistence;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Application.Migrations;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ComputeProfiles;

[Collection(IntegrationTestCollection.Name)]
public sealed class ComputeProfileResolutionTests(MongoReplicaSetFixture fixture)
    : IAsyncLifetime
{
    private static readonly ResourceTypeId WebServiceType =
        ResourceTypeId.Create("pole/web-service");

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Resolve_default_returns_exact_target_snapshot_and_enforces_availability()
    {
        await PrepareDatabaseAsync();
        var target = ComputeProfileTestData.CreateTarget();
        var otherTarget = ComputeProfileTestData.CreateTarget();
        await fixture.DbContext.DeploymentTargets.InsertManyAsync([target, otherTarget]);
        using var host = new ComputeProfilePersistenceTestHost(fixture);
        var profile = (await host.GetRequiredService<PersistCreatedComputeProfile>()(
            ComputeProfileTestData.CreateProfile(
                target.Id,
                isDefault: true,
                tenantAccess: SelectedTenantsComputeProfileAccess.Create(
                    [ComputeProfileTestData.TenantId])))).AssertSuccess();
        var resolve = host.GetRequiredService<ResolveComputeProfile>();

        var resolved = (await resolve(new ComputeProfileResolutionRequest(
            ComputeProfileId: null,
            target.Id,
            ComputeProfileTestData.TenantId,
            WebServiceType))).AssertSuccess();

        Assert.Equal(profile.Id, resolved.ComputeProfileId);
        Assert.Equal(profile.Revision, resolved.ComputeProfileRevision);
        Assert.Equal(target.Id, resolved.DeploymentTargetId);
        Assert.Equal(profile.Resources.Requests.CpuMillis, resolved.Requests.CpuMillis);
        Assert.Equal(profile.ExtendedResources.Single().Name, resolved.ExtendedResources.Single().Name);
        Assert.Equal(profile.TargetMappings.Single().RuntimeClassName, resolved.RuntimeClassName);

        var wrongTenant = await resolve(new ComputeProfileResolutionRequest(
            profile.Id,
            target.Id,
            ComputeProfileTestData.OtherTenantId,
            WebServiceType));
        Assert.IsType<ComputeProfileUnavailable>(wrongTenant.AssertError().Value);

        var wrongResourceType = await resolve(new ComputeProfileResolutionRequest(
            profile.Id,
            target.Id,
            ComputeProfileTestData.TenantId,
            ResourceTypeId.Create("pole/redis")));
        Assert.IsType<ComputeProfileUnavailable>(wrongResourceType.AssertError().Value);

        var missingMapping = await resolve(new ComputeProfileResolutionRequest(
            profile.Id,
            otherTarget.Id,
            ComputeProfileTestData.TenantId,
            WebServiceType));
        Assert.IsType<ComputeProfileUnavailable>(missingMapping.AssertError().Value);
    }

    [Fact]
    public async Task Resolve_many_resolves_default_and_explicit_profiles_for_each_target()
    {
        await PrepareDatabaseAsync();
        var firstTarget = ComputeProfileTestData.CreateTarget();
        var secondTarget = ComputeProfileTestData.CreateTarget();
        await fixture.DbContext.DeploymentTargets.InsertManyAsync([firstTarget, secondTarget]);
        using var host = new ComputeProfilePersistenceTestHost(fixture);
        var create = host.GetRequiredService<PersistCreatedComputeProfile>();
        var defaultProfile = (await create(ComputeProfileTestData.CreateProfile(
            firstTarget.Id,
            isDefault: true))).AssertSuccess();
        var explicitProfile = (await create(ComputeProfileTestData.CreateProfile(
            secondTarget.Id,
            displayName: "Target specific"))).AssertSuccess();
        var resolveMany = host.GetRequiredService<ResolveComputeProfiles>();

        var resolved = (await resolveMany(new ComputeProfileBatchResolutionRequest(
            ComputeProfileTestData.TenantId,
            WebServiceType,
            [
                new ComputeProfileTargetResolutionRequest(firstTarget.Id, null),
                new ComputeProfileTargetResolutionRequest(secondTarget.Id, explicitProfile.Id)
            ]))).AssertSuccess();

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
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        using var host = new ComputeProfilePersistenceTestHost(fixture);
        var create = host.GetRequiredService<PersistCreatedComputeProfile>();
        var update = host.GetRequiredService<PersistUpdatedComputeProfile>();
        var resolve = host.GetRequiredService<ResolveComputeProfile>();
        var fence = host.GetRequiredService<FenceResolvedComputeProfiles>();
        var original = (await create(ComputeProfileTestData.CreateProfile(
                target.Id,
                tenantAccess: SelectedTenantsComputeProfileAccess.Create(
                    [ComputeProfileTestData.TenantId]))))
            .AssertSuccess();
        var resolved = (await resolve(new ComputeProfileResolutionRequest(
            original.Id,
            target.Id,
            ComputeProfileTestData.TenantId,
            WebServiceType))).AssertSuccess();

        var fenced = await FenceInTransactionAsync(
            fence,
            [new ComputeProfileResolutionFenceRequest(
                    resolved.ComputeProfileId,
                    resolved.ComputeProfileRevision,
                    resolved.DeploymentTargetId,
                    ComputeProfileTestData.TenantId,
                    WebServiceType)]);
        fenced.AssertSuccess();

        var afterFence = await LoadAsync(original.Id);
        Assert.Equal(1, afterFence.ResolutionFence);

        var staleReplacement = Reconfigure(original, "Stale update");
        var staleUpdate = await update(original, staleReplacement);
        Assert.IsType<ComputeProfileRevisionConflict>(staleUpdate.AssertError().Value);

        var updated = (await update(
            afterFence,
            Reconfigure(afterFence, "Current update"))).AssertSuccess();
        Assert.Equal(2, updated.Revision);
        Assert.Equal(1, updated.ResolutionFence);

        var staleFence = await FenceInTransactionAsync(
            fence,
            [new ComputeProfileResolutionFenceRequest(
                    resolved.ComputeProfileId,
                    resolved.ComputeProfileRevision,
                    resolved.DeploymentTargetId,
                    ComputeProfileTestData.TenantId,
                    WebServiceType)]);
        Assert.IsType<ComputeProfileResolutionFenceRejected>(staleFence.AssertError());
    }

    [Fact]
    public async Task Fence_many_is_atomic_when_one_profile_snapshot_is_stale()
    {
        await PrepareDatabaseAsync();
        var firstTarget = ComputeProfileTestData.CreateTarget();
        var secondTarget = ComputeProfileTestData.CreateTarget();
        await fixture.DbContext.DeploymentTargets.InsertManyAsync([firstTarget, secondTarget]);
        using var host = new ComputeProfilePersistenceTestHost(fixture);
        var create = host.GetRequiredService<PersistCreatedComputeProfile>();
        var update = host.GetRequiredService<PersistUpdatedComputeProfile>();
        var resolveMany = host.GetRequiredService<ResolveComputeProfiles>();
        var fence = host.GetRequiredService<FenceResolvedComputeProfiles>();
        var first = (await create(ComputeProfileTestData.CreateProfile(firstTarget.Id))).AssertSuccess();
        var second = (await create(ComputeProfileTestData.CreateProfile(
            secondTarget.Id,
            displayName: "Second"))).AssertSuccess();
        var resolved = (await resolveMany(new ComputeProfileBatchResolutionRequest(
            ComputeProfileTestData.TenantId,
            WebServiceType,
            [
                new ComputeProfileTargetResolutionRequest(firstTarget.Id, first.Id),
                new ComputeProfileTargetResolutionRequest(secondTarget.Id, second.Id)
            ]))).AssertSuccess();

        var fenceRequests = resolved
            .Select(profile => new ComputeProfileResolutionFenceRequest(
                profile.ComputeProfileId,
                profile.ComputeProfileRevision,
                profile.DeploymentTargetId,
                ComputeProfileTestData.TenantId,
                WebServiceType))
            .ToArray();
        (await FenceInTransactionAsync(fence, fenceRequests)).AssertSuccess();
        var firstAfterFence = await LoadAsync(first.Id);
        var secondAfterFence = await LoadAsync(second.Id);
        Assert.Equal(1, firstAfterFence.ResolutionFence);
        Assert.Equal(1, secondAfterFence.ResolutionFence);
        _ = (await update(
            firstAfterFence,
            Reconfigure(firstAfterFence, "Changed before batch fence"))).AssertSuccess();

        var fenced = await FenceInTransactionAsync(fence, fenceRequests);

        Assert.IsType<ComputeProfileResolutionFenceRejected>(fenced.AssertError());
        Assert.Equal(1, (await LoadAsync(first.Id)).ResolutionFence);
        Assert.Equal(1, (await LoadAsync(second.Id)).ResolutionFence);
    }

    [Fact]
    public async Task Availability_query_filters_tenant_resource_and_target_without_parallel_array_index()
    {
        await PrepareDatabaseAsync();
        var target = ComputeProfileTestData.CreateTarget();
        var otherTarget = ComputeProfileTestData.CreateTarget();
        await fixture.DbContext.DeploymentTargets.InsertManyAsync([target, otherTarget]);
        using var host = new ComputeProfilePersistenceTestHost(fixture);
        var create = host.GetRequiredService<PersistCreatedComputeProfile>();
        var availableProfile = (await create(ComputeProfileTestData.CreateProfile(
            target.Id,
            tenantAccess: SelectedTenantsComputeProfileAccess.Create(
                [ComputeProfileTestData.TenantId]),
            displayName: "Available"))).AssertSuccess();
        await create(ComputeProfileTestData.CreateProfile(
            target.Id,
            tenantAccess: SelectedTenantsComputeProfileAccess.Create(
                [ComputeProfileTestData.OtherTenantId]),
            displayName: "Other tenant"));
        await create(ComputeProfileTestData.CreateProfile(
            target.Id,
            resourceTypes: [ResourceTypeId.Create("pole/redis")],
            displayName: "Other resource"));
        await create(ComputeProfileTestData.CreateProfile(
            otherTarget.Id,
            displayName: "Other target"));

        var profiles = await host.GetRequiredService<LoadAvailableComputeProfiles>()(
            new ComputeProfileAvailabilityQuery(
                ComputeProfileTestData.TenantId,
                target.Id,
                WebServiceType));

        Assert.Equal([availableProfile.Id], profiles.Select(profile => profile.Id));
    }

    private async Task PrepareDatabaseAsync()
    {
        await new DeploymentTargetReferenceMigration().Up(fixture.Database);
        await new ComputeProfileSchemaValidationMigration().Up(fixture.Database);
        await new ComputeProfileIndexesMigration().Up(fixture.Database);
    }

    private async Task<ComputeProfile> LoadAsync(string id) =>
        await fixture.Database
            .GetCollection<ComputeProfile>("ComputeProfiles")
            .Find(ComputeProfileById(id))
            .SingleAsync();

    private static FilterDefinition<ComputeProfile> ComputeProfileById(string computeProfileId) =>
        Builders<ComputeProfile>.Filter.Eq(profile => profile.Id, computeProfileId);

    private async Task<Result<Unit, ComputeProfileResolutionFenceRejected>>
        FenceInTransactionAsync(
            FenceResolvedComputeProfiles fence,
            IReadOnlyList<ComputeProfileResolutionFenceRequest> requests)
    {
        using var session = await fixture.DbContext.Client.StartSessionAsync();
        session.StartTransaction();
        try
        {
            var result = await fence(requests, session);
            if (result is Ok<Unit>)
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

    private static ComputeProfile Reconfigure(
        ComputeProfile current,
        string description) =>
        current.Reconfigure(
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
