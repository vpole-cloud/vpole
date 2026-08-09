using System.Collections.Immutable;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Primitives;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Workloads;

namespace Pole.Features.WebServices.Application.Common;

public static class WebServiceResourceDeployment
{
    public static ResourceTypeId ResourceType { get; } =
        ResourceTypeId.Create("pole/web-service");
}

internal static class WebServiceResourceDeploymentComputeMappings
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
            compute.Architecture switch
            {
                ComputeArchitecture.Any => ResourceDeploymentComputeArchitecture.Any,
                ComputeArchitecture.Amd64 => ResourceDeploymentComputeArchitecture.Amd64,
                ComputeArchitecture.Arm64 => ResourceDeploymentComputeArchitecture.Arm64,
                _ => throw new ArgumentOutOfRangeException(nameof(compute))
            },
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
                    toleration.Operator switch
                    {
                        ComputeTolerationOperator.Equal => ResourceDeploymentTolerationOperator.Equal,
                        ComputeTolerationOperator.Exists => ResourceDeploymentTolerationOperator.Exists,
                        _ => throw new ArgumentOutOfRangeException(nameof(compute))
                    },
                    toleration.Value,
                    toleration.Effect switch
                    {
                        ComputeTaintEffect.Any => ResourceDeploymentTaintEffect.Any,
                        ComputeTaintEffect.NoSchedule => ResourceDeploymentTaintEffect.NoSchedule,
                        ComputeTaintEffect.PreferNoSchedule => ResourceDeploymentTaintEffect.PreferNoSchedule,
                        ComputeTaintEffect.NoExecute => ResourceDeploymentTaintEffect.NoExecute,
                        _ => throw new ArgumentOutOfRangeException(nameof(compute))
                    },
                    toleration.TolerationSeconds))
                .ToImmutableList(),
            compute.RuntimeClassName);

    private static ResourceDeploymentNodeAffinityTerm ToResourceDeployment(
        ComputeNodeAffinityTermValue term) =>
        new(term.Expressions
            .Select(expression => new ResourceDeploymentNodeAffinityExpression(
                expression.Key,
                expression.Operator switch
                {
                    ComputeNodeAffinityOperator.In => ResourceDeploymentNodeAffinityOperator.In,
                    ComputeNodeAffinityOperator.NotIn => ResourceDeploymentNodeAffinityOperator.NotIn,
                    ComputeNodeAffinityOperator.Exists => ResourceDeploymentNodeAffinityOperator.Exists,
                    ComputeNodeAffinityOperator.DoesNotExist => ResourceDeploymentNodeAffinityOperator.DoesNotExist,
                    ComputeNodeAffinityOperator.GreaterThan => ResourceDeploymentNodeAffinityOperator.GreaterThan,
                    ComputeNodeAffinityOperator.LessThan => ResourceDeploymentNodeAffinityOperator.LessThan,
                    _ => throw new ArgumentOutOfRangeException(nameof(term))
                },
                expression.Values))
            .ToImmutableList());
}
