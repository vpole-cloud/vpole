// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using k8s;
using k8s.Models;
using Pole.Testing;
using Xunit;
using KubernetesClient = k8s.Kubernetes;

namespace Pole.Tests.Kubernetes;

[Collection(KubernetesLabCollection.Name)]
[Trait(TestTraits.Layer, TestTraits.Layers.Kubernetes)]
[Trait(TestTraits.Concern, "MultiCluster")]
[Trait(TestTraits.Scope, TestTraits.Scopes.MultiCluster)]
[Trait(TestTraits.Feature, "DeploymentTargets")]
public sealed class KubernetesLabMultiClusterTests(
    KubernetesClusterFixture fixture
)
{
    [KubernetesFact("multi-cluster")]
    public async Task Same_resource_name_is_isolated_across_workload_targets()
    {
        var targets = new[]
        {
            fixture.State.GetRequiredTarget("workload-a"),
            fixture.State.GetRequiredTarget("workload-b"),
        };
        var identity = fixture.Identity;
        var namespaceName = identity.CreateNamespaceName("multi");
        var clients = targets.Select(target => new KubernetesClient(target.CreateConfiguration())).ToArray();
        await using var resources = new TestResourceScope("multi-cluster namespace isolation");
        resources.Defer(
            "dispose Kubernetes clients",
            _ =>
            {
                foreach (var client in clients)
                    client.Dispose();
                return Task.CompletedTask;
            });
        var namespaces = resources.CreateConcurrentCleanupGroup("owned namespaces");

        for (var index = 0; index < clients.Length; index++)
        {
            await OwnedNamespaceManager.CreateTrackedAsync(namespaces, clients[index], namespaceName, identity);
            await clients[index]
                .CoreV1
                .CreateNamespacedConfigMapAsync(
                    new V1ConfigMap
                    {
                        Metadata = new V1ObjectMeta { Name = "same-name" },
                        Data = new Dictionary<string, string> { ["target"] = targets[index].Key },
                    },
                    namespaceName);
        }

        var values = await Task.WhenAll(
            clients.Select(client => client.CoreV1.ReadNamespacedConfigMapAsync("same-name", namespaceName)));

        Assert.Equal("workload-a", values[0].Data["target"]);
        Assert.Equal("workload-b", values[1].Data["target"]);
        Assert.NotEqual(targets[0].CreateConfiguration().Host, targets[1].CreateConfiguration().Host);
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
        await using var resources = new TestResourceScope("unavailable target isolation");

        await resources.CreateAsync(
            $"delete owned namespace '{namespaceName}'",
            token => OwnedNamespaceManager.CreateAsync(healthy, namespaceName, identity, token),
            (owned, token) => OwnedNamespaceManager.DeleteAsync(healthy, owned, token));
        await healthy.CoreV1.CreateNamespacedConfigMapAsync(
            new V1ConfigMap
            {
                Metadata = new V1ObjectMeta { Name = "healthy" },
                Data = new Dictionary<string, string> { ["revision"] = "1" },
            },
            namespaceName);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            unavailable.CoreV1.ListNamespaceAsync(cancellationToken: timeout.Token));

        var unchanged = await healthy.CoreV1.ReadNamespacedConfigMapAsync("healthy", namespaceName);
        Assert.Equal("1", unchanged.Data["revision"]);
    }
}
