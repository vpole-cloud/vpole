using Pole.ResultType;
using k8s;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Application.Persistence;
using Pole.Encryption;
using Pole.Features.DeploymentTargets.Application;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Bootstrap;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Application.Persistence;
using Pole.Features.DeploymentTargets.Application.Persistence.Credentials;
using Pole.Features.DeploymentTargets.Application.Persistence.Mutations;
using Pole.Features.DeploymentTargets.Application.Persistence.Reads;
using Pole.Features.DeploymentTargets.Application.Persistence.References;
using Pole.Features.DeploymentTargets.Application.Validation;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.Features.Functions.Runtime;
using Pole.Features.ResourceGroups.Application;
using Pole.Features.ResourceGroups.Application.Endpoints;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceDeployments.Application.Observation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.StorageProfiles.Application;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.WebServices.Application.Kubernetes;
using Pole.Server.Extensions;
using Pole.Platform.Hosting;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.DeploymentTargets;

[Collection(IntegrationTestCollection.Name)]
public sealed class DeploymentTargetProductionDiTests(MongoReplicaSetFixture fixture)
    : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void KubernetesConsumersDependOnProvider_NotProcessWideClient()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DeploymentTargets:Bootstrap:Enabled"] = "false"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddKubernetes(configuration)
            .AddDeploymentTargetsFeature(configuration);

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IKubernetes));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IKubernetesClientProvider) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType ==
                typeof(AdmitDeploymentTargetPublicIngressForNewWorkload) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(AdmitDeploymentTargetForWorkload) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType ==
                typeof(AdmitDeploymentTargetPublicIngressForDomainReservation) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(ApplyDeploymentTargetReferenceChanges) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(LoadDeploymentTargetById) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(LoadDefaultDeploymentTargetId) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType ==
                typeof(EnsureNoOverlappingDeploymentTargetPublicBaseDomain) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType ==
                typeof(EnsureNoDeploymentTargetPublicBaseDomainReferences) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(LookupDeploymentTarget) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(LookupDefaultDeploymentTarget) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(AdmitDefaultDeploymentTargetForNewWorkload) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(FenceDeploymentTargetWorkloadConfigurations) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(PrepareDeploymentTargetConnection) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(CreateDeploymentTargetKubernetesClient) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(ProbeDeploymentTargetConnection) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(InvalidateDeploymentTargetKubernetesClient) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(BootstrapDeploymentTargets) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType.Name.Contains("DeploymentTargetDbContext", StringComparison.Ordinal));

        var consumerAssemblies = new[]
        {
            typeof(ResourceGroupApplicationRegistration).Assembly,
            typeof(WebServiceResourceDeploymentAdapter).Assembly,
            typeof(FunctionRevisionKubernetesManager).Assembly,
            typeof(StorageProfileApplicationRegistration).Assembly
        };
        var directClientConsumers = consumerAssemblies
            .Distinct()
            .SelectMany(assembly => assembly.GetExportedTypes())
            .Where(type => type.GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .Any(parameter => parameter.ParameterType == typeof(IKubernetes)))
            .Select(type => type.FullName)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(directClientConsumers);
    }

    [Fact]
    public async Task KubernetesConsumers_DoNotMaskProviderInfrastructureFailures()
    {
        var expected = new TestProviderInfrastructureException();
        var provider = new ThrowingKubernetesClientProvider(expected);
        var target = DeploymentTargetNetworkPolicyTests.Target().AttestForTests().ToLookupDto() with { Id = DeploymentTargetId };
        var services = new ServiceCollection();
        services.AddSingleton<IMongoClient>(fixture.Client);
        services.AddSingleton<IMongoDatabase>(fixture.Database);
        services.AddSingleton<MongoRunTransaction>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<LookupDeploymentTarget>((_, _) =>
            Task.FromResult<LookupDeploymentTargetOutcome>(new(target)));
        services.AddSingleton<FenceDeploymentTargetObservation>((_, _, _) =>
            Task.FromResult<FenceDeploymentTargetObservationOutcome>(new(new DeploymentTargetObservationFenced())));
        services.AddSingleton<IResourceDeploymentObservationReadBudget, ResourceDeploymentObservationReadBudget>();
        services.AddOutboxPersistence();
        services.AddSingleton<IKubernetesClientProvider>(provider);
        services.AddStorageProfilesFeature();
        using var serviceProvider = services.BuildServiceProvider(validateScopes: true);
        using var scope = serviceProvider.CreateScope();
        var discoverStorageClasses =
            scope.ServiceProvider.GetRequiredService<DiscoverKubernetesStorageClasses>();

        var storageFailure = await Assert.ThrowsAsync<TestProviderInfrastructureException>(
            () => discoverStorageClasses(
                DeploymentTargetId));
        var configuredTarget = target with
        {
            WorkloadConfiguration = target.WorkloadConfiguration with
            {
                NetworkSources = target.WorkloadConfiguration.NetworkSources with
                {
                    Entries = target.WorkloadConfiguration.NetworkSources.Entries.SetItem(
                        "functions/gateway", new DeploymentTargetCidrNetworkSourceDto("192.0.2.9/32"))
                }
            }
        };
        var functionFailure = await Assert.ThrowsAsync<TestProviderInfrastructureException>(
            () => new FunctionRevisionKubernetesManager(
                provider,
                (id, ct) => Task.FromResult<LookupDeploymentTargetOutcome>(
                    new LookupDeploymentTargetOutcome(configuredTarget)),
                (tenantId, runtimeCellId, targetId, kubernetesNamespace, ct) =>
                    Task.FromResult<EnsureRuntimeCellIngressBaselineOutcome>(
                        new RuntimeCellIngressBaselineReady()),
                []).ApplyAsync(
                    new FunctionRevisionInKubernetes(
                        null!,
                        null!,
                        new FunctionResourcePlacement(
                            "507f1f77bcf86cd799439091",
                            "507f1f77bcf86cd799439092",
                            DeploymentTargetId,
                            "functions",
                            "507f1f77bcf86cd799439044"))));

        Assert.Same(expected, storageFailure);
        Assert.Equal([DeploymentTargetId, DeploymentTargetId], provider.RequestedTargetIds);
        Assert.Same(expected, functionFailure);
    }

    [Fact]
    public void FeatureRegistration_ResolvesTheProductionCapabilityGraph()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DeploymentTargets:Bootstrap:Enabled"] = "false"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMongoClient>(fixture.Client);
        services.AddSingleton<IMongoDatabase>(fixture.Database);
        services.AddSingleton<IEnvelopeEncryptionService>(TestEnvelopeEncryption.Create());
        services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<MongoRunTransaction>();
        services.AddOutboxPersistence();
        services.AddKubernetes(configuration)
            .AddDeploymentTargetsFeature(configuration);
        services.AddResourceGroupsFeature(configuration);
        services.AddFunctionRuntimeFeature(configuration);

        // AddKubernetes also contributes unrelated feature registrations. Resolve every
        // DeploymentTargets capability below instead of validating an intentionally partial host.
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        AssertResolved<IFunctionRevisionKubernetesManager>(provider);
        AssertResolved<EnsureRuntimeCellIngressBaseline>(provider);
        AssertResolved<PrepareDeploymentTargetConnection>(provider);
        AssertResolved<CreateDeploymentTargetKubernetesClient>(provider);
        AssertResolved<ProbeDeploymentTargetConnection>(provider);
        AssertResolved<InvalidateDeploymentTargetKubernetesClient>(provider);
        AssertResolved<BootstrapDeploymentTargets>(provider);

        AssertResolved<LoadDeploymentTargetById>(scope.ServiceProvider);
        AssertResolved<LoadDefaultDeploymentTargetId>(scope.ServiceProvider);
        AssertResolved<EnsureNoOverlappingDeploymentTargetPublicBaseDomain>(scope.ServiceProvider);
        AssertResolved<EnsureNoDeploymentTargetPublicBaseDomainReferences>(scope.ServiceProvider);
        AssertResolved<LoadDeploymentTargets>(scope.ServiceProvider);
        AssertResolved<LoadEligibleDeploymentTargets>(scope.ServiceProvider);
        AssertResolved<PersistCreatedDeploymentTarget>(scope.ServiceProvider);
        AssertResolved<PersistUpdatedDeploymentTarget>(scope.ServiceProvider);
        AssertResolved<PersistRotatedDeploymentTargetConnection>(scope.ServiceProvider);
        AssertResolved<PersistDeploymentTargetValidation>(scope.ServiceProvider);
        AssertResolved<EnsureNoDeploymentTargetReferences>(scope.ServiceProvider);
        AssertResolved<DeleteDeploymentTarget>(scope.ServiceProvider);
        AssertResolved<LookupDeploymentTarget>(scope.ServiceProvider);
        AssertResolved<LookupDefaultDeploymentTarget>(scope.ServiceProvider);
        AssertResolved<ListEligibleDeploymentTargetsForNewWorkload>(scope.ServiceProvider);
        AssertResolved<AdmitDeploymentTargetForWorkload>(scope.ServiceProvider);
        AssertResolved<AdmitDefaultDeploymentTargetForNewWorkload>(scope.ServiceProvider);
        AssertResolved<AdmitDeploymentTargetForNewWorkload>(scope.ServiceProvider);
        AssertResolved<AdmitDeploymentTargetPublicIngressForNewWorkload>(scope.ServiceProvider);
        AssertResolved<ListEligiblePublicIngressDeploymentTargetsForNewWorkload>(scope.ServiceProvider);
        AssertResolved<AdmitDeploymentTargetPublicIngressForDomainReservation>(scope.ServiceProvider);
        AssertResolved<ApplyDeploymentTargetReferenceChanges>(scope.ServiceProvider);
        AssertResolved<FenceDeploymentTargetWorkloadConfigurations>(scope.ServiceProvider);
        AssertResolved<RewrapDeploymentTargetCredentials>(scope.ServiceProvider);
    }

    private static void AssertResolved<T>(IServiceProvider provider)
        where T : notnull =>
        Assert.NotNull(provider.GetRequiredService<T>());

    private sealed class ThrowingKubernetesClientProvider(Exception exception)
        : IKubernetesClientProvider
    {
        public List<string> RequestedTargetIds { get; } = [];

        public Task<IKubernetes> GetAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default)
        {
            RequestedTargetIds.Add(deploymentTargetId);
            return Task.FromException<IKubernetes>(exception);
        }

        public Task<IKubernetes> GetForCleanupAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default) =>
            GetAsync(deploymentTargetId, cancellationToken);

        public Task<IKubernetes> GetDefaultAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<IKubernetes>(exception);
    }

    private const string DeploymentTargetId = "507f1f77bcf86cd799439098";

    private sealed class TestProviderInfrastructureException : Exception;

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Pole.Tests.Integration";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

}
