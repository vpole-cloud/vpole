using k8s;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.DeploymentTargets.Contracts;
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

        var consumerAssemblies = new[]
        {
            typeof(ProvisionResourceGroupOnResourceGroupCreatedConsumer).Assembly,
            typeof(CreateRedisServiceHandler).Assembly,
            typeof(KubernetesWebServiceDeploymentApplier).Assembly,
            typeof(KubernetesPostgresClusterSecretStore).Assembly,
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
            () => new KubernetesStorageDiscoveryService(provider).GetStorageClassesAsync());
        var functionFailure = await Assert.ThrowsAsync<TestProviderInfrastructureException>(
            () => new FunctionRevisionKubernetesManager(provider, []).ApplyAsync(default));
        var webServiceFailure = await Assert.ThrowsAsync<TestProviderInfrastructureException>(
            () => new KubernetesWebServiceDeploymentApplier(provider, null!).ApplyAsync(null!));

        Assert.Same(expected, storageFailure);
        Assert.Same(expected, functionFailure);
        Assert.Same(expected, webServiceFailure);
    }

    private sealed class ThrowingKubernetesClientProvider(Exception exception)
        : IKubernetesClientProvider
    {
        public Task<IKubernetes> GetAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default) =>
            Task.FromException<IKubernetes>(exception);

        public Task<IKubernetes> GetDefaultAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<IKubernetes>(exception);
    }

    private sealed class TestProviderInfrastructureException : Exception;
}
