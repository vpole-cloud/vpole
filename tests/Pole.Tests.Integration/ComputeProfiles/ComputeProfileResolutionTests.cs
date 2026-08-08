using MongoDB.Driver;
using Pole.Database;
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
        Assert.IsType<SemanticValidationFailure>(wrongTenant.AssertFailure());

        var wrongResourceType = await resolve(new ComputeProfileResolutionRequest(
            profile.Id,
            target.Id,
            ComputeProfileTestData.TenantId,
            ResourceTypeId.Create("pole/redis")));
        Assert.IsType<SemanticValidationFailure>(wrongResourceType.AssertFailure());

        var missingMapping = await resolve(new ComputeProfileResolutionRequest(
            profile.Id,
            otherTarget.Id,
            ComputeProfileTestData.TenantId,
            WebServiceType));
        Assert.IsType<SemanticValidationFailure>(missingMapping.AssertFailure());
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
        var fence = host.GetRequiredService<FenceResolvedComputeProfile>();
        var runTransaction = host.GetRequiredService<RunMongoTransaction>();
        var original = (await create(ComputeProfileTestData.CreateProfile(target.Id)))
            .AssertSuccess();
        var resolved = (await resolve(new ComputeProfileResolutionRequest(
            original.Id,
            target.Id,
            ComputeProfileTestData.TenantId,
            WebServiceType))).AssertSuccess();

        var fenced = await runTransaction(
            (session, ct) => fence(
                resolved,
                ComputeProfileTestData.TenantId,
                WebServiceType,
                session,
                ct));
        fenced.AssertSuccess();

        var afterFence = await LoadAsync(original.Id);
        Assert.Equal(1, afterFence.ResolutionFence);

        var staleReplacement = Reconfigure(original, "Stale update");
        var staleUpdate = await update(original, staleReplacement);
        Assert.IsType<ConflictFailure>(staleUpdate.AssertFailure());

        var updated = (await update(
            afterFence,
            Reconfigure(afterFence, "Current update"))).AssertSuccess();
        Assert.Equal(2, updated.Revision);
        Assert.Equal(1, updated.ResolutionFence);

        var staleFence = await runTransaction(
            (session, ct) => fence(
                resolved,
                ComputeProfileTestData.TenantId,
                WebServiceType,
                session,
                ct));
        Assert.IsType<ConflictFailure>((Failure)staleFence);
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
            .Find(profile => profile.Id == id)
            .SingleAsync();

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
