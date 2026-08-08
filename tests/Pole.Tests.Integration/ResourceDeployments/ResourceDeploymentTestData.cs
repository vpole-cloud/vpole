using System.Collections.Immutable;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Tests.Integration.ResourceDeployments;

internal static class ResourceDeploymentTestData
{
    public const string ComputeProfileId = "507f1f77bcf86cd799439019";

    public static ResolvedComputeConfiguration Compute(
        long revision = 1,
        long requestsCpuMillis = 250,
        long requestsMemoryMi = 256,
        long limitsCpuMillis = 1000,
        long limitsMemoryMi = 1024) =>
        ResolvedComputeConfiguration.Create(
            ComputeProfileId,
            revision,
            "General purpose",
            ResolvedComputeResourceValues.Create(
                requestsCpuMillis,
                requestsMemoryMi),
            ResolvedComputeResourceValues.Create(
                limitsCpuMillis,
                limitsMemoryMi),
            ResolvedComputeArchitecture.Any);

    public static ResolvedComputeProfile ResolvedComputeProfile(
        string deploymentTargetId,
        long revision = 1,
        long requestsCpuMillis = 250,
        long requestsMemoryMi = 256,
        long limitsCpuMillis = 1000,
        long limitsMemoryMi = 1024) =>
        new(
            ComputeProfileId,
            revision,
            deploymentTargetId,
            "General purpose",
            new ComputeResourceValues(
                requestsCpuMillis,
                requestsMemoryMi),
            new ComputeResourceValues(
                limitsCpuMillis,
                limitsMemoryMi),
            ComputeArchitecture.Any,
            ImmutableList<ComputeExtendedResourceValue>.Empty,
            ImmutableList<ComputeNodeSelectorValue>.Empty,
            ImmutableList<ComputeNodeAffinityTermValue>.Empty,
            ImmutableList<ComputePreferredNodeAffinityTermValue>.Empty,
            ImmutableList<ComputeTolerationValue>.Empty,
            RuntimeClassName: null);
}
