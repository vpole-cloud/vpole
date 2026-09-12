using k8s;
using Pole.Testing;
using Xunit;

namespace Pole.Tests.Kubernetes;

[Collection(KubernetesLabCollection.Name)]
[Trait(TestTraits.Layer, TestTraits.Layers.Kubernetes)]
[Trait(TestTraits.Concern, "Topology")]
[Trait(TestTraits.Scope, TestTraits.Scopes.Topology)]
[Trait(TestTraits.Feature, "ComputeProfiles")]
public sealed class KubernetesLabTopologyTests(KubernetesClusterFixture fixture)
{
    [KubernetesFact("topology")]
    public async Task Topology_profile_has_deterministic_compute_pools_and_taint()
    {
        var nodes = await fixture.Client.CoreV1.ListNodeAsync();
        var pools = nodes.Items
            .Where(node => node.Metadata.Labels.ContainsKey("pole.dev/compute-pool"))
            .ToDictionary(
                node => node.Metadata.Labels["pole.dev/compute-pool"],
                node => node,
                StringComparer.Ordinal);

        Assert.Equal(["general", "isolated", "memory"], pools.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(
            pools["isolated"].Spec.Taints,
            taint => taint.Key == "pole.dev/dedicated" &&
                     taint.Value == "isolated" &&
                     taint.Effect == "NoSchedule");
    }

    [KubernetesFact("topology")]
    public async Task Node_selector_and_toleration_place_workloads_in_the_expected_pools()
    {
        var identity = fixture.Identity;
        var namespaceName = identity.CreateNamespaceName("topology");
        var ownedNamespace = await OwnedNamespaceManager.CreateAsync(
            fixture.Client,
            namespaceName,
            identity);

        try
        {
            var nodes = await fixture.Client.CoreV1.ListNodeAsync();
            var architecture = nodes.Items
                .Select(node => node.Metadata.Labels["kubernetes.io/arch"])
                .Distinct(StringComparer.Ordinal)
                .Single();
            await fixture.Client.CoreV1.CreateNamespacedPodAsync(
                BuildPod("general", "general", architecture, useAffinity: false, tolerateIsolated: false),
                namespaceName);
            await fixture.Client.CoreV1.CreateNamespacedPodAsync(
                BuildPod("isolated", "isolated", architecture, useAffinity: true, tolerateIsolated: true),
                namespaceName);

            var general = await WaitForSchedulingAsync("general", namespaceName);
            var isolated = await WaitForSchedulingAsync("isolated", namespaceName);
            var generalNode = await fixture.Client.CoreV1.ReadNodeAsync(general.Spec.NodeName);
            var isolatedNode = await fixture.Client.CoreV1.ReadNodeAsync(isolated.Spec.NodeName);

            Assert.Equal("general", generalNode.Metadata.Labels["pole.dev/compute-pool"]);
            Assert.Equal("isolated", isolatedNode.Metadata.Labels["pole.dev/compute-pool"]);
        }
        finally
        {
            await OwnedNamespaceManager.DeleteAsync(fixture.Client, ownedNamespace);
        }
    }

    private static k8s.Models.V1Pod BuildPod(
        string name,
        string pool,
        string architecture,
        bool useAffinity,
        bool tolerateIsolated) =>
        new()
        {
            Metadata = new k8s.Models.V1ObjectMeta { Name = name },
            Spec = new k8s.Models.V1PodSpec
            {
                Containers =
                [
                    new k8s.Models.V1Container
                    {
                        Name = name,
                        Image = "registry.k8s.io/pause:3.10"
                    }
                ],
                NodeSelector = new Dictionary<string, string>
                {
                    ["kubernetes.io/arch"] = architecture,
                    ["pole.dev/compute-pool"] = useAffinity ? "isolated" : pool
                },
                Affinity = useAffinity
                    ? new k8s.Models.V1Affinity
                    {
                        NodeAffinity = new k8s.Models.V1NodeAffinity
                        {
                            RequiredDuringSchedulingIgnoredDuringExecution = new k8s.Models.V1NodeSelector
                            {
                                NodeSelectorTerms =
                                [
                                    new k8s.Models.V1NodeSelectorTerm
                                    {
                                        MatchExpressions =
                                        [
                                            new k8s.Models.V1NodeSelectorRequirement
                                            {
                                                Key = "pole.dev/compute-pool",
                                                OperatorProperty = "In",
                                                Values = [pool]
                                            }
                                        ]
                                    }
                                ]
                            }
                        }
                    }
                    : null,
                Tolerations = tolerateIsolated
                    ?
                    [
                        new k8s.Models.V1Toleration
                        {
                            Key = "pole.dev/dedicated",
                            OperatorProperty = "Equal",
                            Value = "isolated",
                            Effect = "NoSchedule"
                        }
                    ]
                    : null
            }
        };

    private async Task<k8s.Models.V1Pod> WaitForSchedulingAsync(string name, string namespaceName)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var pod = await fixture.Client.CoreV1.ReadNamespacedPodAsync(name, namespaceName);
            if (!string.IsNullOrWhiteSpace(pod.Spec.NodeName))
                return pod;
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new TimeoutException($"Pod '{name}' was not scheduled in '{namespaceName}'.");
    }
}
