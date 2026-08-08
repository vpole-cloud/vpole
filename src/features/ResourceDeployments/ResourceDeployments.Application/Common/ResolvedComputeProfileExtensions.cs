using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Common;

internal static class ResolvedComputeProfileExtensions
{
    public static ResolvedComputeConfiguration ToConfiguration(
        this ResolvedComputeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return ResolvedComputeConfiguration.Create(
            profile.ComputeProfileId,
            profile.ComputeProfileRevision,
            profile.DisplayName,
            ResolvedComputeResourceValues.Create(
                profile.Requests.CpuMillis,
                profile.Requests.MemoryMi),
            ResolvedComputeResourceValues.Create(
                profile.Limits.CpuMillis,
                profile.Limits.MemoryMi),
            profile.Architecture switch
            {
                ComputeArchitecture.Any => ResolvedComputeArchitecture.Any,
                ComputeArchitecture.Amd64 => ResolvedComputeArchitecture.Amd64,
                ComputeArchitecture.Arm64 => ResolvedComputeArchitecture.Arm64,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(profile),
                    profile.Architecture,
                    "Unsupported compute architecture.")
            },
            profile.ExtendedResources.Select(resource =>
                ResolvedComputeExtendedResource.Create(
                    resource.Name,
                    resource.Quantity)),
            profile.NodeSelector.Select(selector =>
                ResolvedComputeNodeSelector.Create(
                    selector.Key,
                    selector.Value)),
            profile.RequiredNodeAffinity.Select(ToConfiguration),
            profile.PreferredNodeAffinity.Select(term =>
                ResolvedComputePreferredNodeAffinityTerm.Create(
                    term.Weight,
                    ToConfiguration(term.Term))),
            profile.Tolerations.Select(toleration =>
                ResolvedComputeToleration.Create(
                    toleration.Key,
                    toleration.Operator switch
                    {
                        ComputeTolerationOperator.Equal =>
                            ResolvedComputeTolerationOperator.Equal,
                        ComputeTolerationOperator.Exists =>
                            ResolvedComputeTolerationOperator.Exists,
                        _ => throw new ArgumentOutOfRangeException(
                            nameof(profile),
                            toleration.Operator,
                            "Unsupported toleration operator.")
                    },
                    toleration.Value,
                    toleration.Effect switch
                    {
                        ComputeTaintEffect.Any => ResolvedComputeTaintEffect.Any,
                        ComputeTaintEffect.NoSchedule =>
                            ResolvedComputeTaintEffect.NoSchedule,
                        ComputeTaintEffect.PreferNoSchedule =>
                            ResolvedComputeTaintEffect.PreferNoSchedule,
                        ComputeTaintEffect.NoExecute =>
                            ResolvedComputeTaintEffect.NoExecute,
                        _ => throw new ArgumentOutOfRangeException(
                            nameof(profile),
                            toleration.Effect,
                            "Unsupported taint effect.")
                    },
                    toleration.TolerationSeconds)),
            profile.RuntimeClassName);
    }

    private static ResolvedComputeNodeAffinityTerm ToConfiguration(
        ComputeNodeAffinityTermValue term) =>
        ResolvedComputeNodeAffinityTerm.Create(
            term.Expressions.Select(expression =>
                ResolvedComputeNodeAffinityExpression.Create(
                    expression.Key,
                    expression.Operator switch
                    {
                        ComputeNodeAffinityOperator.In =>
                            ResolvedComputeNodeAffinityOperator.In,
                        ComputeNodeAffinityOperator.NotIn =>
                            ResolvedComputeNodeAffinityOperator.NotIn,
                        ComputeNodeAffinityOperator.Exists =>
                            ResolvedComputeNodeAffinityOperator.Exists,
                        ComputeNodeAffinityOperator.DoesNotExist =>
                            ResolvedComputeNodeAffinityOperator.DoesNotExist,
                        ComputeNodeAffinityOperator.GreaterThan =>
                            ResolvedComputeNodeAffinityOperator.GreaterThan,
                        ComputeNodeAffinityOperator.LessThan =>
                            ResolvedComputeNodeAffinityOperator.LessThan,
                        _ => throw new ArgumentOutOfRangeException(
                            nameof(term),
                            expression.Operator,
                            "Unsupported node-affinity operator.")
                    },
                    expression.Values)));
}
