// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.Functions.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Workloads;

namespace Pole.Features.Functions.Application.Common;

public static class FunctionResourceDeployment
{
    public const string GatewaySourceName = "functions/gateway";

    public static ResourceTypeId ResourceType { get; } = ResourceTypeId.Create("pole/function-app");
}

internal static class FunctionComputeResourceDeploymentMappings
{
    public static ResourceDeploymentComputeConfiguration
        ToFunctionResourceDeploymentConfiguration(this ResolvedComputeProfile compute, FunctionResources resources) =>
        new(
            compute.ComputeProfileId,
            compute.ComputeProfileRevision,
            compute.DeploymentTargetId,
            compute.DisplayName,
            new ResourceDeploymentComputeResourceValues(resources.Requests.CpuMillis, resources.Requests.MemoryMi),
            new ResourceDeploymentComputeResourceValues(resources.Limits.CpuMillis, resources.Limits.MemoryMi),
            compute.Architecture,
            compute.ExtendedResources
                .Select(resource => new ResourceDeploymentExtendedResource(resource.Name, resource.Quantity))
                .ToImmutableList(),
            compute.NodeSelector
                .Select(selector => new ResourceDeploymentNodeSelector(selector.Key, selector.Value))
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

    private static ResourceDeploymentNodeAffinityTerm ToResourceDeployment(ComputeNodeAffinityTermValue term) => new(
        term.Expressions
            .Select(expression => new ResourceDeploymentNodeAffinityExpression(
                expression.Key,
                expression.Operator,
                expression.Values))
            .ToImmutableList());
}
