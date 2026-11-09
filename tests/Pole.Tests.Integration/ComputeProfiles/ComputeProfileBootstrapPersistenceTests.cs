using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Application.Bootstrap;
using Pole.Features.ComputeProfiles.Application.Catalog;
using Pole.Features.ComputeProfiles.Migrations;
using Pole.Features.DeploymentTargets.Application.Admission;
using Pole.Features.DeploymentTargets.Application.Catalog;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.DeploymentTargets.Migrations;
using Pole.ResultType;
using Pole.Tests.Integration.DeploymentTargets;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ComputeProfiles;

[Collection(IntegrationTestCollection.Global3)]
public sealed class ComputeProfileBootstrapPersistenceTests(MongoReplicaSetFixture fixture)
    : MongoIntegrationTestBase(fixture)
{
    public override Task InitializeAsync() => base.InitializeAsync();

    public override Task DisposeAsync() => base.DisposeAsync();

    [Fact]
    public async Task Bootstrap_before_attestation_persists_one_profile_without_admitting_workloads()
    {
        await new ComputeProfileIndexesMigration().Up(Fixture.Database);
        await new DeploymentTargetReferenceMigration().Up(Fixture.Database);
        var target = DeploymentTargetNetworkPolicyTests.Target();
        await Fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        LoadDefaultDeploymentTargetId loadDefaultId = _ => Task.FromResult<string?>(target.Id);
        LoadDeploymentTargetById loadTarget = new MongoLoadDeploymentTargetById(
            Fixture.DbContext.DeploymentTargets).InvokeAsync;
        var admission = new MongoAdmitDefaultDeploymentTargetForNewWorkload(loadDefaultId, loadTarget);
        var lookup = new MongoLookupDefaultDeploymentTarget(loadDefaultId, loadTarget);
        using var persistence = new ComputeProfilePersistenceTestHost(Fixture);
        var services = new ServiceCollection();
        services.AddSingleton(persistence.GetRequiredService<LoadDefaultComputeProfile>());
        services.AddSingleton(persistence.GetRequiredService<PersistCreatedComputeProfile>());
        services.AddSingleton<AdmitDefaultDeploymentTargetForNewWorkload>(admission.InvokeAsync);
        services.AddSingleton<LookupDefaultDeploymentTarget>(lookup.InvokeAsync);
        await using var provider = services.BuildServiceProvider();
        var bootstrapper = new ComputeProfileBootstrapper(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<ComputeProfileBootstrapper>.Instance);

        await bootstrapper.BootstrapAsync();
        await bootstrapper.BootstrapAsync();

        var profile = await persistence.GetRequiredService<LoadDefaultComputeProfile>()();
        Assert.NotNull(profile);
        Assert.Equal(target.Id, Assert.Single(profile.TargetMappings).DeploymentTargetId);
        var reference = Assert.Single(await Fixture.DbContext.DeploymentTargetReferences
            .Find(Builders<DeploymentTargetReference>.Filter.Empty).ToListAsync());
        Assert.Equal(profile.Id, reference.OwnerId);
        Assert.Equal(target.Id, reference.DeploymentTargetId);
        var persistedTarget = Assert.IsType<DeploymentTarget>(await loadTarget(target.Id));
        Assert.Equal(target.ReferenceFence + 1, persistedTarget.ReferenceFence);
        Assert.IsType<UnattestedNetworkPolicy>(persistedTarget.WorkloadConfiguration.NetworkPolicy);
        Assert.IsType<DefaultDeploymentTargetWorkloadAdmissionError>((await admission.InvokeAsync()).Value);
    }
    [Theory]
    [InlineData("runtime-cell", true, true, false)]
    [InlineData("compute-profile", false, true, false)]
    [InlineData("compute-profile", true, false, false)]
    [InlineData("compute-profile", true, true, true)]
    public async Task Configuration_exception_preserves_runtime_and_target_lifecycle_fences(
        string ownerType,
        bool enabled,
        bool acceptsWorkloads,
        bool deleting)
    {
        await new DeploymentTargetReferenceMigration().Up(Fixture.Database);
        var target = DeploymentTargetNetworkPolicyTests.Target() with
        {
            Enabled = enabled,
            AcceptsWorkloads = acceptsWorkloads,
            DeletionRequestedAtUtc = deleting ? DateTime.UtcNow : null
        };
        await Fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        using var persistence = new ComputeProfilePersistenceTestHost(Fixture);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();

        var result = await persistence.GetRequiredService<ApplyDeploymentTargetReferenceChanges>()(
            new ApplyDeploymentTargetReferenceChangesRequest(
                Owner: new DeploymentTargetReferenceOwner(ownerType, target.Id),
                AddedDeploymentTargetIds: [target.Id],
                RemovedDeploymentTargetIds: []),
            session: session);

        Assert.IsType<DeploymentTargetReferenceChangeRejected>(result.Value);
        await session.AbortTransactionAsync();
        Assert.Equal(0, await Fixture.DbContext.DeploymentTargetReferences.CountDocumentsAsync(
            Builders<DeploymentTargetReference>.Filter.Empty));
    }

}
