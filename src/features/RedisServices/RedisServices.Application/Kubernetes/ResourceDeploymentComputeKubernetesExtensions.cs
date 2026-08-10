using Pole.Features.Core.Kubernetes;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Workloads;

namespace Pole.Features.RedisServices.Application.Kubernetes;

internal static class ResourceDeploymentComputeKubernetesExtensions
{
    public static KubernetesComputeConfiguration ToKubernetesCompute(
        this ResourceDeploymentComputeConfiguration compute) =>
        new(
            new(compute.Requests.CpuMillis, compute.Requests.MemoryMi),
            new(compute.Limits.CpuMillis, compute.Limits.MemoryMi),
            compute.Architecture,
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
                toleration.Operator,
                toleration.Value,
                toleration.Effect,
                toleration.TolerationSeconds)).ToList(),
            compute.RuntimeClassName);

    private static KubernetesNodeAffinityTerm ToKubernetes(
        ResourceDeploymentNodeAffinityTerm term) =>
        new(term.Expressions.Select(expression => new KubernetesNodeAffinityExpression(
            expression.Key,
            expression.Operator,
            expression.Values)).ToList());
}
