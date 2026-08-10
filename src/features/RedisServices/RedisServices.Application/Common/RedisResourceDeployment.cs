using System.Collections.Immutable;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.RedisServices.Application.Common;

public static class RedisResourceDeployment
{
    public static ResourceTypeId ResourceType { get; } =
        ResourceTypeId.Create("pole/redis");
}

internal static class ComputeProfileResourceDeploymentMappings
{
    public static ResourceDeploymentComputeConfiguration ToResourceDeploymentConfiguration(
        this ResolvedComputeProfile compute) =>
        new(
            compute.ComputeProfileId,
            compute.ComputeProfileRevision,
            compute.DeploymentTargetId,
            compute.DisplayName,
            new ResourceDeploymentComputeResourceValues(
                compute.Requests.CpuMillis,
                compute.Requests.MemoryMi),
            new ResourceDeploymentComputeResourceValues(
                compute.Limits.CpuMillis,
                compute.Limits.MemoryMi),
            compute.Architecture,
            compute.ExtendedResources
                .Select(resource => new ResourceDeploymentExtendedResource(
                    resource.Name,
                    resource.Quantity))
                .ToImmutableList(),
            compute.NodeSelector
                .Select(selector => new ResourceDeploymentNodeSelector(
                    selector.Key,
                    selector.Value))
                .ToImmutableList(),
            compute.RequiredNodeAffinity.Select(ToResourceDeployment).ToImmutableList(),
            compute.PreferredNodeAffinity
                .Select(term => new ResourceDeploymentPreferredNodeAffinityTerm(
                    term.Weight,
                    ToResourceDeployment(term.Term)))
                .ToImmutableList(),
            compute.Tolerations
                .Select(toleration => new ResourceDeploymentToleration(
                    toleration.Key,
                    toleration.Operator,
                    toleration.Value,
                    toleration.Effect,
                    toleration.TolerationSeconds))
                .ToImmutableList(),
            compute.RuntimeClassName);

    private static ResourceDeploymentNodeAffinityTerm ToResourceDeployment(
        ComputeNodeAffinityTermValue term) =>
        new(term.Expressions
            .Select(expression => new ResourceDeploymentNodeAffinityExpression(
                expression.Key,
                expression.Operator,
                expression.Values))
            .ToImmutableList());
}
