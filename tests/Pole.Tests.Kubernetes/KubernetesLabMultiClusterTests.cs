using k8s;
using Pole.Testing;
using Xunit;
using KubernetesClient = k8s.Kubernetes;

namespace Pole.Tests.Kubernetes;

[Collection(KubernetesLabCollection.Name)]
[Trait(TestTraits.Layer, TestTraits.Layers.Kubernetes)]
[Trait(TestTraits.Concern, "MultiCluster")]
[Trait(TestTraits.Scope, TestTraits.Scopes.MultiCluster)]
[Trait(TestTraits.Feature, "DeploymentTargets")]
public sealed class KubernetesLabMultiClusterTests(KubernetesClusterFixture fixture)
{
    [KubernetesFact("multi-cluster")]
    public async Task Same_resource_name_is_isolated_across_workload_targets()
    {
        var targets = new[]
        {
            fixture.State.GetRequiredTarget("workload-a"),
            fixture.State.GetRequiredTarget("workload-b")
        };
        var identity = fixture.Identity;
        var namespaceName = identity.CreateNamespaceName("multi");
        var clients = targets.Select(target => new KubernetesClient(target.CreateConfiguration())).ToArray();
        var ownedNamespaces = new List<(IKubernetes Client, OwnedNamespace Namespace)>();

        try
        {
            for (var index = 0; index < clients.Length; index++)
            {
                var owned = await OwnedNamespaceManager.CreateAsync(clients[index], namespaceName, identity);
                ownedNamespaces.Add((clients[index], owned));
                await clients[index].CoreV1.CreateNamespacedConfigMapAsync(
                    new k8s.Models.V1ConfigMap
                    {
                        Metadata = new k8s.Models.V1ObjectMeta { Name = "same-name" },
                        Data = new Dictionary<string, string> { ["target"] = targets[index].Key }
                    },
                    namespaceName);
            }

            var values = await Task.WhenAll(clients.Select(client =>
                client.CoreV1.ReadNamespacedConfigMapAsync("same-name", namespaceName)));

            Assert.Equal("workload-a", values[0].Data["target"]);
            Assert.Equal("workload-b", values[1].Data["target"]);
            Assert.NotEqual(targets[0].CreateConfiguration().Host, targets[1].CreateConfiguration().Host);
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            try
            {
                foreach (var owned in ownedNamespaces)
                {
                    try
                    {
                        await OwnedNamespaceManager.DeleteAsync(owned.Client, owned.Namespace);
                    }
                    catch (Exception exception)
                    {
                        cleanupFailures.Add(exception);
                    }
                }
            }
            finally
            {
                foreach (var client in clients)
                    client.Dispose();
            }
            if (cleanupFailures.Count > 0)
                throw new AggregateException("Multi-cluster namespace cleanup failed.", cleanupFailures);
        }
    }

    [KubernetesFact("multi-cluster")]
    public async Task Unavailable_target_does_not_mutate_the_healthy_target()
    {
        var healthyTarget = fixture.State.GetRequiredTarget("workload-a");
        var unavailableConfiguration = fixture.State.GetRequiredTarget("workload-b").CreateConfiguration();
        unavailableConfiguration.Host = "http://127.0.0.1:1";
        using var healthy = new KubernetesClient(healthyTarget.CreateConfiguration());
        using var unavailable = new KubernetesClient(unavailableConfiguration);
        var identity = fixture.Identity;
        var namespaceName = identity.CreateNamespaceName("partial");
        OwnedNamespace? ownedNamespace = null;

        try
        {
            ownedNamespace = await OwnedNamespaceManager.CreateAsync(
                healthy,
                namespaceName,
                identity);
            await healthy.CoreV1.CreateNamespacedConfigMapAsync(
                new k8s.Models.V1ConfigMap
                {
                    Metadata = new k8s.Models.V1ObjectMeta { Name = "healthy" },
                    Data = new Dictionary<string, string> { ["revision"] = "1" }
                },
                namespaceName);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<Exception>(() =>
                unavailable.CoreV1.ListNamespaceAsync(cancellationToken: timeout.Token));

            var unchanged = await healthy.CoreV1.ReadNamespacedConfigMapAsync("healthy", namespaceName);
            Assert.Equal("1", unchanged.Data["revision"]);
        }
        finally
        {
            if (ownedNamespace is not null)
                await OwnedNamespaceManager.DeleteAsync(healthy, ownedNamespace);
        }
    }
}
