using k8s;
using Xunit;

namespace Pole.Features.Postgres.Tests.Integration.Infrastructure;

public sealed class KubernetesClusterFixture : IAsyncLifetime
{
    public IKubernetes Client { get; private set; } = null!;

    public Task InitializeAsync()
    {
        var config = KubernetesClientConfiguration.BuildConfigFromConfigFile();
        Client = new Kubernetes(config);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Client.Dispose();
        return Task.CompletedTask;
    }
}
