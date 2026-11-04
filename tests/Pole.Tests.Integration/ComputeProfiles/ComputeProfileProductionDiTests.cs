using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Application.Persistence;
using Pole.Features.ComputeProfiles.Application;
using Pole.Features.ComputeProfiles.Application.Bootstrap;
using Pole.Features.ComputeProfiles.Application.Persistence;
using Pole.Features.ComputeProfiles.Application.Persistence.Mutations;
using Pole.Features.ComputeProfiles.Application.Persistence.Reads;
using Pole.Features.ComputeProfiles.Application.Validation;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Tenants.Contracts;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ComputeProfiles;

[Collection(IntegrationTestCollection.Name)]
public sealed class ComputeProfileProductionDiTests(MongoReplicaSetFixture fixture)
{
    [Fact]
    public void FeatureRegistration_ResolvesTheComputeProfileCapabilityGraph()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMongoClient>(fixture.Client);
        services.AddSingleton<IMongoDatabase>(fixture.Database);
        services.AddSingleton<MongoRunTransaction>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IKubernetesClientProvider>(
            new FixedKubernetesClientProvider(null!));
        services.AddSingleton<EnsureTenantsExist>((_, _) => Task.FromResult<EnsureTenantsExistOutcome>(
            new TenantsExist()));
        services.AddSingleton<AdmitDeploymentTargetForWorkload>((_, _) =>
            Task.FromException<AdmitDeploymentTargetForWorkloadOutcome>(
                new NotSupportedException("The DI graph test does not invoke admission.")));
        services.AddSingleton<ApplyDeploymentTargetReferenceChanges>((request, _, _) =>
            Task.FromResult<ApplyDeploymentTargetReferenceChangesOutcome>(new DeploymentTargetReferencesChanged()));
        services.AddSingleton<EnsureComputeProfileUnreferenced>((_, _, _) =>
            Task.FromResult<EnsureComputeProfileUnreferencedOutcome>(
                new ComputeProfileUnreferenced()));
        services.AddOutboxPersistence();
        services.AddComputeProfilesFeature();

        Assert.DoesNotContain(services,
            descriptor =>
                descriptor.ServiceType.Name.Contains("Legacy", StringComparison.Ordinal));
        Assert.Contains(services,
            descriptor =>
                descriptor.ServiceType == typeof(ResolveComputeProfile) &&
                descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services,
            descriptor =>
                descriptor.ServiceType == typeof(ResolveComputeProfiles) &&
                descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services,
            descriptor =>
                descriptor.ServiceType == typeof(FenceResolvedComputeProfiles) &&
                descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services,
            descriptor =>
                descriptor.ServiceType == typeof(BootstrapComputeProfiles) &&
                descriptor.Lifetime == ServiceLifetime.Singleton);

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        AssertResolved<LoadComputeProfileById>(scope.ServiceProvider);
        AssertResolved<LoadDefaultComputeProfile>(scope.ServiceProvider);
        AssertResolved<LoadComputeProfilePage>(scope.ServiceProvider);
        AssertResolved<LoadAvailableComputeProfiles>(scope.ServiceProvider);
        AssertResolved<PersistCreatedComputeProfile>(scope.ServiceProvider);
        AssertResolved<PersistUpdatedComputeProfile>(scope.ServiceProvider);
        AssertResolved<DeleteComputeProfile>(scope.ServiceProvider);
        AssertResolved<ResolveComputeProfile>(scope.ServiceProvider);
        AssertResolved<ResolveComputeProfiles>(scope.ServiceProvider);
        AssertResolved<FenceResolvedComputeProfiles>(scope.ServiceProvider);
        AssertResolved<ValidateComputeProfileTenantAccess>(scope.ServiceProvider);
        AssertResolved<ValidateComputeProfileTargetMappings>(scope.ServiceProvider);
        AssertResolved<BootstrapComputeProfiles>(provider);
    }

    private static void AssertResolved<T>(IServiceProvider provider)
        where T : notnull =>
        Assert.NotNull(provider.GetRequiredService<T>());
}
