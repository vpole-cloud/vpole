// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using k8s.Models;
using Pole.Features.Core.Kubernetes;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

[Trait("Layer", "Manifest")]
[Trait("Concern", "Rendering")]
[Trait("Scope", "ManifestOnly")]
[Trait("Feature", "ResourceDeployments")]
public sealed class ResolvedComputeConfigurationKubernetesTests
{
    [Fact]
    public void Mapping_ProducesResourcesAndCompletePodScheduling()
    {
        var requiredTerm = new KubernetesNodeAffinityTerm(
        [
            new KubernetesNodeAffinityExpression(
                "topology.kubernetes.io/zone",
                ComputeNodeAffinityOperator.In,
                ["ua-1", "ua-2"]),
        ]);
        var compute = new KubernetesComputeConfiguration(
            new KubernetesComputeResourceValues(750, 768),
            new KubernetesComputeResourceValues(2000, 2048),
            ComputeArchitecture.Arm64,
            [new KubernetesExtendedResource("nvidia.com/gpu", 1)],
            [new KubernetesNodeSelector("kubernetes.io/os", "linux")],
            [requiredTerm],
            [new KubernetesPreferredNodeAffinityTerm(75, requiredTerm)],
            [
                new KubernetesToleration(
                    "dedicated",
                    ComputeTolerationOperator.Equal,
                    "gpu",
                    ComputeTaintEffect.NoSchedule,
                    TolerationSeconds: null),
            ],
            RuntimeClassName: "kata");

        var resources = compute.ToResourceRequirements();
        var podSpec = new V1PodSpec();
        compute.ApplyTo(podSpec);

        Assert.Equal("750m", resources.Requests["cpu"].ToString());
        Assert.Equal("768Mi", resources.Requests["memory"].ToString());
        Assert.Equal("1", resources.Requests["nvidia.com/gpu"].ToString());
        Assert.Equal("1", resources.Limits["nvidia.com/gpu"].ToString());
        Assert.Equal("linux", podSpec.NodeSelector["kubernetes.io/os"]);
        Assert.Equal("arm64", podSpec.NodeSelector["kubernetes.io/arch"]);
        Assert.Equal("kata", podSpec.RuntimeClassName);

        var required = Assert.Single(
            podSpec.Affinity.NodeAffinity.RequiredDuringSchedulingIgnoredDuringExecution.NodeSelectorTerms);
        var expression = Assert.Single(required.MatchExpressions);
        Assert.Equal("topology.kubernetes.io/zone", expression.Key);
        Assert.Equal("In", expression.OperatorProperty);
        Assert.Equal(["ua-1", "ua-2"], expression.Values);
        Assert.Equal(
            75,
            Assert.Single(podSpec.Affinity.NodeAffinity.PreferredDuringSchedulingIgnoredDuringExecution).Weight);

        var toleration = Assert.Single(podSpec.Tolerations);
        Assert.Equal("dedicated", toleration.Key);
        Assert.Equal("Equal", toleration.OperatorProperty);
        Assert.Equal("gpu", toleration.Value);
        Assert.Equal("NoSchedule", toleration.Effect);
    }

    [Fact]
    public void EmptyScheduling_DoesNotAddEmptyKubernetesObjects()
    {
        var compute = new KubernetesComputeConfiguration(
            new KubernetesComputeResourceValues(250, 256),
            new KubernetesComputeResourceValues(1000, 1024),
            ComputeArchitecture.Any,
            [],
            [],
            [],
            [],
            [],
            RuntimeClassName: null);
        var podSpec = new V1PodSpec();

        compute.ApplyTo(podSpec);

        Assert.Null(podSpec.NodeSelector);
        Assert.Null(podSpec.Affinity);
        Assert.Null(podSpec.Tolerations);
        Assert.Null(podSpec.RuntimeClassName);
    }
}
