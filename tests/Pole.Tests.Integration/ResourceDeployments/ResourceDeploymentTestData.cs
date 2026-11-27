// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
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
        long limitsMemoryMi = 1024) => ResolvedComputeConfiguration.Create(
        ComputeProfileId,
        revision,
        "General purpose",
        ResolvedComputeResourceValues.Create(requestsCpuMillis, requestsMemoryMi),
        ResolvedComputeResourceValues.Create(limitsCpuMillis, limitsMemoryMi),
        ComputeArchitecture.Any);

    public static ResolvedComputeProfile ResolvedComputeProfile(
        string deploymentTargetId,
        long revision = 1,
        long requestsCpuMillis = 250,
        long requestsMemoryMi = 256,
        long limitsCpuMillis = 1000,
        long limitsMemoryMi = 1024) => new(
        ComputeProfileId,
        revision,
        deploymentTargetId,
        "General purpose",
        new ComputeResourceValues(requestsCpuMillis, requestsMemoryMi),
        new ComputeResourceValues(limitsCpuMillis, limitsMemoryMi),
        ComputeArchitecture.Any,
        ImmutableList<ComputeExtendedResourceValue>.Empty,
        ImmutableList<ComputeNodeSelectorValue>.Empty,
        ImmutableList<ComputeNodeAffinityTermValue>.Empty,
        ImmutableList<ComputePreferredNodeAffinityTermValue>.Empty,
        ImmutableList<ComputeTolerationValue>.Empty,
        RuntimeClassName: null);

    public static ResourceDeploymentComputeConfiguration ContractCompute(
        string deploymentTargetId,
        long revision = 1,
        long requestsCpuMillis = 250,
        long requestsMemoryMi = 256,
        long limitsCpuMillis = 1000,
        long limitsMemoryMi = 1024) => new(
        ComputeProfileId,
        revision,
        deploymentTargetId,
        "General purpose",
        new ResourceDeploymentComputeResourceValues(requestsCpuMillis, requestsMemoryMi),
        new ResourceDeploymentComputeResourceValues(limitsCpuMillis, limitsMemoryMi),
        ComputeArchitecture.Any,
        ImmutableList<ResourceDeploymentExtendedResource>.Empty,
        ImmutableList<ResourceDeploymentNodeSelector>.Empty,
        ImmutableList<ResourceDeploymentNodeAffinityTerm>.Empty,
        ImmutableList<ResourceDeploymentPreferredNodeAffinityTerm>.Empty,
        ImmutableList<ResourceDeploymentToleration>.Empty,
        RuntimeClassName: null);
}
