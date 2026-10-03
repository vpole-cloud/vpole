using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.Postgres.Application;
using Pole.Features.Postgres.Application.Kubernetes;
using Pole.Features.ResourceDeployments.Contracts;
using Xunit;

namespace Pole.Features.Postgres.Tests.Integration;

public sealed class PostgresRegistrationTests
{
    [Fact]
    public void Registration_PreservesOtherProviderAndAddsPostgresAdapter()
    {
        var services = new ServiceCollection();
        var other = ResourceDeploymentAdapterDescriptor.Create<OtherAdapter>(
            ResourceTypeId.Create("pole/test-provider"), ResourcePlacementCapability.SingleTarget);
        services.AddSingleton(other);
        services.AddPostgresFeature(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var descriptors = provider.GetServices<ResourceDeploymentAdapterDescriptor>().ToArray();
        Assert.Contains(other, descriptors);
        var postgres = Assert.Single(descriptors, descriptor => descriptor.ResourceType.Value == "pole/postgresql");
        Assert.Equal(typeof(PostgresResourceDeploymentAdapter), postgres.AdapterType);
    }

    private sealed class OtherAdapter : IResourceDeploymentAdapter
    {
        public Task<ResourceDeploymentAdapterResult> ApplyAsync(
            ResourceDeploymentReconciliationContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<ResourceDeploymentAdapterResult>(ResourceDeploymentAdapterResult.Succeeded.Create());

        public Task<ResourceDeploymentAdapterResult> DeleteAsync(
            ResourceDeploymentReconciliationContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<ResourceDeploymentAdapterResult>(ResourceDeploymentAdapterResult.Succeeded.Create());
    }
}
