using k8s;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Application;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.Features.Postgres.Application.Kubernetes;
using Pole.Features.RedisServices.Application.Endpoints;
using Pole.Features.ResourceGroups.Application.Endpoints;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.WebServices.Application.Kubernetes;
using Pole.Server.Extensions;
using Xunit;

namespace Pole.Tests.Integration.DeploymentTargets;

public sealed class DeploymentTargetProductionDiTests
{
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
            descriptor.ServiceType ==
                typeof(AdmitDeploymentTargetPublicIngressForDomainReservation) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(DeploymentTargetLookup) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);

        services.AddSingleton<IDeploymentTargetDbContext, ResolveOnlyDeploymentTargetDbContext>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider
            .GetRequiredService<AdmitDeploymentTargetPublicIngressForNewWorkload>());
        Assert.NotNull(scope.ServiceProvider
            .GetRequiredService<AdmitDeploymentTargetPublicIngressForDomainReservation>());

        var consumerAssemblies = new[]
        {
            typeof(ProvisionResourceGroupOnResourceGroupCreatedConsumer).Assembly,
            typeof(CreateRedisServiceHandler).Assembly,
            typeof(WebServiceResourceDeploymentAdapter).Assembly,
            typeof(PostgresResourceDeploymentAdapter).Assembly,
            typeof(FunctionRevisionKubernetesManager).Assembly,
            typeof(KubernetesStorageDiscoveryService).Assembly
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

        var storageFailure = await Assert.ThrowsAsync<TestProviderInfrastructureException>(
            () => new KubernetesStorageDiscoveryService(provider).GetStorageClassesAsync(
                DeploymentTargetId));
        var functionFailure = await Assert.ThrowsAsync<TestProviderInfrastructureException>(
            () => new FunctionRevisionKubernetesManager(provider, []).ApplyAsync(default));

        Assert.Same(expected, storageFailure);
        Assert.Equal([DeploymentTargetId], provider.RequestedTargetIds);
        Assert.Same(expected, functionFailure);
    }

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

        public Task<IKubernetes> GetDefaultAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<IKubernetes>(exception);
    }

    private const string DeploymentTargetId = "507f1f77bcf86cd799439098";

    private sealed class TestProviderInfrastructureException : Exception;

    private sealed class ResolveOnlyDeploymentTargetDbContext : IDeploymentTargetDbContext
    {
        public IMongoClient Client => throw new NotSupportedException();

        public IMongoDatabase Database => throw new NotSupportedException();

        public IMongoCollection<DeploymentTarget> DeploymentTargets =>
            throw new NotSupportedException();

        public IMongoCollection<DeploymentTargetReference> DeploymentTargetReferences =>
            throw new NotSupportedException();

        public IMongoCollection<DeploymentTargetSettings> DeploymentTargetSettings =>
            throw new NotSupportedException();
    }
}
