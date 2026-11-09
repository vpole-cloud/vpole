using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Application;
using Pole.Features.ComputeProfiles.Application.Bootstrap;
using Pole.Features.ComputeProfiles.Application.Catalog;
using Pole.Features.ComputeProfiles.Application.Validation;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Outbox.Application;
using Pole.Features.Outbox.Application.Mongo;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Tenants.Contracts;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Pole.Testing.Kubernetes;
using Xunit;

namespace Pole.Tests.Integration.ComputeProfiles;

public sealed class ComputeProfileProductionDiTests
{
    private readonly IMongoDatabase database = new MongoClient("mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100").GetDatabase("di-only");
    [Fact]
    public void FeatureRegistration_ResolvesTheComputeProfileCapabilityGraph()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMongoClient>(database.Client);
        services.AddSingleton<IMongoDatabase>(database);
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
