using Pole.Features.Core.Kubernetes;
using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.WebServices.Application.Kubernetes;

internal static class ResourceDeploymentComputeKubernetesExtensions
{
    public static KubernetesComputeConfiguration ToKubernetesCompute(
        this ResourceDeploymentComputeConfiguration compute) =>
        new(
            new(compute.Requests.CpuMillis, compute.Requests.MemoryMi),
            new(compute.Limits.CpuMillis, compute.Limits.MemoryMi),
            compute.Architecture switch
            {
                ResourceDeploymentComputeArchitecture.Any => KubernetesComputeArchitecture.Any,
                ResourceDeploymentComputeArchitecture.Amd64 => KubernetesComputeArchitecture.Amd64,
                ResourceDeploymentComputeArchitecture.Arm64 => KubernetesComputeArchitecture.Arm64,
                _ => throw new ArgumentOutOfRangeException(nameof(compute))
            },
            compute.ExtendedResources
                .Select(resource => new KubernetesExtendedResource(resource.Name, resource.Quantity))
                .ToList(),
            compute.NodeSelector
                .Select(selector => new KubernetesNodeSelector(selector.Key, selector.Value))
                .ToList(),
            compute.RequiredNodeAffinity.Select(ToKubernetes).ToList(),
            compute.PreferredNodeAffinity.Select(term =>
                new KubernetesPreferredNodeAffinityTerm(term.Weight, ToKubernetes(term.Term))).ToList(),
            compute.Tolerations.Select(toleration => new KubernetesToleration(
                toleration.Key,
                toleration.Operator switch
                {
                    ResourceDeploymentTolerationOperator.Equal => KubernetesTolerationOperator.Equal,
                    ResourceDeploymentTolerationOperator.Exists => KubernetesTolerationOperator.Exists,
                    _ => throw new ArgumentOutOfRangeException(nameof(compute))
                },
                toleration.Value,
                toleration.Effect switch
                {
                    ResourceDeploymentTaintEffect.Any => KubernetesTaintEffect.Any,
                    ResourceDeploymentTaintEffect.NoSchedule => KubernetesTaintEffect.NoSchedule,
                    ResourceDeploymentTaintEffect.PreferNoSchedule => KubernetesTaintEffect.PreferNoSchedule,
                    ResourceDeploymentTaintEffect.NoExecute => KubernetesTaintEffect.NoExecute,
                    _ => throw new ArgumentOutOfRangeException(nameof(compute))
                },
                toleration.TolerationSeconds)).ToList(),
            compute.RuntimeClassName);

    private static KubernetesNodeAffinityTerm ToKubernetes(
        ResourceDeploymentNodeAffinityTerm term) =>
        new(term.Expressions.Select(expression => new KubernetesNodeAffinityExpression(
            expression.Key,
            expression.Operator switch
            {
                ResourceDeploymentNodeAffinityOperator.In => KubernetesNodeAffinityOperator.In,
                ResourceDeploymentNodeAffinityOperator.NotIn => KubernetesNodeAffinityOperator.NotIn,
                ResourceDeploymentNodeAffinityOperator.Exists => KubernetesNodeAffinityOperator.Exists,
                ResourceDeploymentNodeAffinityOperator.DoesNotExist => KubernetesNodeAffinityOperator.DoesNotExist,
                ResourceDeploymentNodeAffinityOperator.GreaterThan => KubernetesNodeAffinityOperator.GreaterThan,
                ResourceDeploymentNodeAffinityOperator.LessThan => KubernetesNodeAffinityOperator.LessThan,
                _ => throw new ArgumentOutOfRangeException(nameof(term))
            },
            expression.Values)).ToList());
}
