using k8s;
using k8s.Autorest;
using k8s.Models;
using Xunit;

namespace Pole.Features.Postgres.Tests.Integration.Infrastructure;

[Collection(KubernetesIntegrationTestCollection.Name)]
public abstract class KubernetesIntegrationTestBase(
    KubernetesClusterFixture fixture) : IAsyncLifetime
{
    protected IKubernetes Kubernetes => fixture.Client;
    protected string NamespaceName { get; private set; } = null!;

    public virtual async Task InitializeAsync()
    {
        NamespaceName = $"pole-test-{Guid.NewGuid():N}"[..24];

        await Kubernetes.CoreV1.CreateNamespaceAsync(new V1Namespace
        {
            Metadata = new V1ObjectMeta
            {
                Name = NamespaceName
            }
        });
    }

    public virtual async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(NamespaceName))
        {
            return;
        }

        try
        {
            await Kubernetes.CoreV1.DeleteNamespaceAsync(NamespaceName);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
        }
    }
}
