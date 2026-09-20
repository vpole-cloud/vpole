using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Application.Bootstrap;
using Pole.Features.ComputeProfiles.Application.Migrations;
using Pole.Features.ComputeProfiles.Application.Persistence.Mutations;
using Pole.Features.ComputeProfiles.Application.Persistence.Reads;
using Pole.Features.DeploymentTargets.Application.Migrations;
using Pole.Features.DeploymentTargets.Application.Persistence.Admission;
using Pole.Features.DeploymentTargets.Application.Persistence.Reads;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.DeploymentTargets;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ComputeProfiles;

[Collection(IntegrationTestCollection.Name)]
public sealed class ComputeProfileBootstrapPersistenceTests(MongoReplicaSetFixture fixture)
    : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Bootstrap_before_attestation_persists_one_profile_without_admitting_workloads()
    {
        await new ComputeProfileIndexesMigration().Up(fixture.Database);
        await new DeploymentTargetReferenceMigration().Up(fixture.Database);
        var target = DeploymentTargetNetworkPolicyTests.Target();
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        LoadDefaultDeploymentTargetId loadDefaultId = _ => Task.FromResult<string?>(target.Id);
        LoadDeploymentTargetById loadTarget = new MongoLoadDeploymentTargetById(
            fixture.DbContext.DeploymentTargets).InvokeAsync;
        var admission = new MongoAdmitDefaultDeploymentTargetForNewWorkload(loadDefaultId, loadTarget);
        var lookup = new MongoLookupDefaultDeploymentTarget(loadDefaultId, loadTarget);
        using var persistence = new ComputeProfilePersistenceTestHost(fixture);
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
        var reference = Assert.Single(await fixture.DbContext.DeploymentTargetReferences
            .Find(Builders<DeploymentTargetReference>.Filter.Empty).ToListAsync());
        Assert.Equal(profile.Id, reference.OwnerId);
        Assert.Equal(target.Id, reference.DeploymentTargetId);
        var persistedTarget = Assert.IsType<DeploymentTarget>(await loadTarget(target.Id));
        Assert.Equal(target.ReferenceFence + 1, persistedTarget.ReferenceFence);
        Assert.IsType<UnattestedNetworkPolicy>(persistedTarget.WorkloadConfiguration.NetworkPolicy);
        Assert.False((await admission.InvokeAsync()).TryGetValue(out _, out _));
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
        await new DeploymentTargetReferenceMigration().Up(fixture.Database);
        var target = DeploymentTargetNetworkPolicyTests.Target() with
        {
            Enabled = enabled,
            AcceptsWorkloads = acceptsWorkloads,
            DeletionRequestedAtUtc = deleting ? DateTime.UtcNow : null
        };
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        using var persistence = new ComputeProfilePersistenceTestHost(fixture);
        using var session = await fixture.Client.StartSessionAsync();
        session.StartTransaction();

        var result = await persistence.GetRequiredService<ApplyDeploymentTargetReferenceChanges>()(
            new DeploymentTargetReferenceOwner(ownerType, target.Id),
            [target.Id],
            [],
            session);

        Assert.IsType<Err<DeploymentTargetReferenceChangeRejected>>(result.Value);
        await session.AbortTransactionAsync();
        Assert.Equal(0, await fixture.DbContext.DeploymentTargetReferences.CountDocumentsAsync(
            Builders<DeploymentTargetReference>.Filter.Empty));
    }

}
