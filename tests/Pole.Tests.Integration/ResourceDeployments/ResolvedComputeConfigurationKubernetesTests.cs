using k8s.Models;
using Pole.Features.ResourceDeployments.Application.Kubernetes;
using Pole.Features.ResourceDeployments.Domain;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

public sealed class ResolvedComputeConfigurationKubernetesTests
{
    [Fact]
    public void Mapping_ProducesResourcesAndCompletePodScheduling()
    {
        var requiredTerm = ResolvedComputeNodeAffinityTerm.Create(
        [
            ResolvedComputeNodeAffinityExpression.Create(
                "topology.kubernetes.io/zone",
                ResolvedComputeNodeAffinityOperator.In,
                ["ua-1", "ua-2"])
        ]);
        var compute = ResolvedComputeConfiguration.Create(
            ResourceDeploymentTestData.ComputeProfileId,
            computeProfileRevision: 3,
            "GPU workload",
            ResolvedComputeResourceValues.Create(750, 768),
            ResolvedComputeResourceValues.Create(2000, 2048),
            ResolvedComputeArchitecture.Arm64,
            [ResolvedComputeExtendedResource.Create("nvidia.com/gpu", 1)],
            [ResolvedComputeNodeSelector.Create("kubernetes.io/os", "linux")],
            [requiredTerm],
            [ResolvedComputePreferredNodeAffinityTerm.Create(75, requiredTerm)],
            [
                ResolvedComputeToleration.Create(
                    "dedicated",
                    ResolvedComputeTolerationOperator.Equal,
                    "gpu",
                    ResolvedComputeTaintEffect.NoSchedule,
                    tolerationSeconds: null)
            ],
            runtimeClassName: "kata");

        var resources = compute.ToKubernetesResources();
        var podSpec = new V1PodSpec();
        compute.ApplyToPodSpec(podSpec);

        Assert.Equal("750m", resources.Requests["cpu"].ToString());
        Assert.Equal("768Mi", resources.Requests["memory"].ToString());
        Assert.Equal("1", resources.Requests["nvidia.com/gpu"].ToString());
        Assert.Equal("1", resources.Limits["nvidia.com/gpu"].ToString());
        Assert.Equal("linux", podSpec.NodeSelector["kubernetes.io/os"]);
        Assert.Equal("arm64", podSpec.NodeSelector["kubernetes.io/arch"]);
        Assert.Equal("kata", podSpec.RuntimeClassName);

        var required = Assert.Single(
            podSpec.Affinity.NodeAffinity
                .RequiredDuringSchedulingIgnoredDuringExecution
                .NodeSelectorTerms);
        var expression = Assert.Single(required.MatchExpressions);
        Assert.Equal("topology.kubernetes.io/zone", expression.Key);
        Assert.Equal("In", expression.OperatorProperty);
        Assert.Equal(["ua-1", "ua-2"], expression.Values);
        Assert.Equal(
            75,
            Assert.Single(podSpec.Affinity.NodeAffinity
                .PreferredDuringSchedulingIgnoredDuringExecution).Weight);

        var toleration = Assert.Single(podSpec.Tolerations);
        Assert.Equal("dedicated", toleration.Key);
        Assert.Equal("Equal", toleration.OperatorProperty);
        Assert.Equal("gpu", toleration.Value);
        Assert.Equal("NoSchedule", toleration.Effect);
    }

    [Fact]
    public void EmptyScheduling_DoesNotAddEmptyKubernetesObjects()
    {
        var compute = ResourceDeploymentTestData.Compute();
        var podSpec = new V1PodSpec();

        compute.ApplyToPodSpec(podSpec);

        Assert.Null(podSpec.NodeSelector);
        Assert.Null(podSpec.Affinity);
        Assert.Null(podSpec.Tolerations);
        Assert.Null(podSpec.RuntimeClassName);
    }
}
