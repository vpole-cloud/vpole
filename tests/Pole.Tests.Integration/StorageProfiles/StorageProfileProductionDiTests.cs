using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Application.Persistence;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.StorageProfiles.Application;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.StorageProfiles.Application.Persistence;
using Pole.Features.StorageProfiles.Application.Persistence.Mutations;
using Pole.Features.StorageProfiles.Application.Persistence.Reads;
using Pole.Features.StorageProfiles.Application.Validation;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Application.Observation;
using Pole.Features.StorageProfiles.Application.Observation;
using Pole.Features.StorageProfiles.Application.Discovery.Observation;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.StorageProfiles;

[Collection(IntegrationTestCollection.Name)]
public sealed class StorageProfileProductionDiTests(MongoReplicaSetFixture fixture)
{
    [Fact]
    public void FeatureRegistration_ResolvesTheStorageProfileCapabilityGraph()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMongoClient>(fixture.Client);
        services.AddSingleton<IMongoDatabase>(fixture.Database);
        services.AddSingleton<MongoRunTransaction>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IKubernetesClientProvider>(
            new FixedKubernetesClientProvider(null!));
        services.AddSingleton<LookupDeploymentTarget>((id, _) => Task.FromResult<LookupDeploymentTargetOutcome>(new DeploymentTargetLookupError(new DeploymentTargetNotFound(id))));
        services.AddSingleton<FenceDeploymentTargetObservation>((_, _, _) => Task.FromResult<FenceDeploymentTargetObservationOutcome>(new DeploymentTargetObservationFenced()));
        services.AddSingleton<IResourceDeploymentObservationReadBudget, ResourceDeploymentObservationReadBudget>();
        services.AddOutboxPersistence();
        services.AddStorageProfilesFeature();

        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType.Name.Contains("Legacy", StringComparison.Ordinal));
        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType.Name.Contains("StorageProfileDbContext", StringComparison.Ordinal));

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        AssertResolved<LoadStorageProfileById>(scope.ServiceProvider);
        AssertResolved<LoadStorageProfiles>(scope.ServiceProvider);
        AssertResolved<LoadAvailableStorageProfiles>(scope.ServiceProvider);
        AssertResolved<PersistCreatedStorageProfile>(scope.ServiceProvider);
        AssertResolved<PersistUpdatedStorageProfile>(scope.ServiceProvider);
        AssertResolved<ResolveStorageProfile>(scope.ServiceProvider);
        AssertResolved<FenceStorageProfileResolution>(scope.ServiceProvider);
        AssertResolved<DiscoverKubernetesStorageClasses>(scope.ServiceProvider);
        AssertResolved<DiscoverKubernetesStorageClassNames>(scope.ServiceProvider);
        AssertResolved<DiscoverKubernetesPersistentVolumes>(scope.ServiceProvider);
        AssertResolved<DiscoverKubernetesPersistentVolumeClaims>(scope.ServiceProvider);
        AssertResolved<ValidateStorageProfileTargetMappings>(scope.ServiceProvider);
        AssertResolved<IStorageObservationStore>(scope.ServiceProvider);
        AssertResolved<IStorageObservationSource>(scope.ServiceProvider);
        AssertResolved<StorageObservationReader>(scope.ServiceProvider);
        AssertResolved<StorageObservationNativeReader>(scope.ServiceProvider);
    }

    private static void AssertResolved<T>(IServiceProvider provider)
        where T : notnull =>
        Assert.NotNull(provider.GetRequiredService<T>());
}
