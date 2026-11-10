using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Outbox.Application;
using Pole.Features.StorageProfiles.Application;
using Pole.Features.StorageProfiles.Application.Catalog;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.StorageProfiles.Application.Discovery.Observation;
using Pole.Features.StorageProfiles.Application.Observation;
using Pole.Features.StorageProfiles.Application.Validation;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Observation;
using Pole.Testing.Kubernetes;
using Xunit;

namespace Pole.Tests.Integration.StorageProfiles;

public sealed class StorageProfileProductionDiTests
{
    private readonly IMongoDatabase database
        = new MongoClient("mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100").GetDatabase("di-only");

    [Fact]
    public void FeatureRegistration_ResolvesTheStorageProfileCapabilityGraph()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMongoClient>(database.Client);
        services.AddSingleton(database);
        services.AddSingleton<MongoRunTransaction>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IKubernetesClientProvider>(new FixedKubernetesClientProvider(null!));
        services.AddSingleton<LookupDeploymentTarget>((id, _) =>
            Task.FromResult<LookupDeploymentTargetOutcome>(
                new DeploymentTargetLookupError(new DeploymentTargetNotFound(id))));
        services.AddSingleton<FenceDeploymentTargetObservation>((
            _, _,
            _) => Task.FromResult<FenceDeploymentTargetObservationOutcome>(new DeploymentTargetObservationFenced()));
        services.AddSingleton<IObservationReadBudget, ObservationReadBudget>();
        services.AddOutboxPersistence();
        services.AddStorageProfilesFeature();

        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType.Name.Contains("Legacy", StringComparison.Ordinal));
        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType.Name.Contains("StorageProfileDbContext", StringComparison.Ordinal));

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
        Assert.Same(
            provider.GetRequiredService<StorageObservationMetrics>(),
            scope.ServiceProvider.GetRequiredService<StorageObservationMetrics>());
    }

    private static void AssertResolved<T>(IServiceProvider provider)
        where T : notnull => Assert.NotNull(provider.GetRequiredService<T>());
}
