// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Common;

internal static class ResourceDeploymentContractMappings
{
    public static ResolvedComputeConfiguration ToDomain(this ResourceDeploymentComputeConfiguration compute)
    {
        ArgumentNullException.ThrowIfNull(compute);

        return ResolvedComputeConfiguration.Create(
            compute.ComputeProfileId,
            compute.ComputeProfileRevision,
            compute.DisplayName,
            ResolvedComputeResourceValues.Create(compute.Requests.CpuMillis, compute.Requests.MemoryMi),
            ResolvedComputeResourceValues.Create(compute.Limits.CpuMillis, compute.Limits.MemoryMi),
            compute.Architecture,
            compute.ExtendedResources.Select(resource =>
                ResolvedComputeExtendedResource.Create(resource.Name, resource.Quantity)),
            compute.NodeSelector.Select(selector => ResolvedComputeNodeSelector.Create(selector.Key, selector.Value)),
            compute.RequiredNodeAffinity.Select(ToDomain),
            compute.PreferredNodeAffinity.Select(term =>
                ResolvedComputePreferredNodeAffinityTerm.Create(term.Weight, ToDomain(term.Term))),
            compute.Tolerations.Select(toleration => ResolvedComputeToleration.Create(
                toleration.Key,
                toleration.Operator,
                toleration.Value,
                toleration.Effect,
                toleration.TolerationSeconds)),
            compute.RuntimeClassName);
    }

    public static ResolvedIngressConfiguration? ToDomain(this ResourceDeploymentIngressConfiguration? ingress) =>
        ingress is null
            ? null
            : ResolvedIngressConfiguration.Create(
                ingress.IngressClassName,
                ingress.ClusterIssuerName,
                ingress.PublicBaseDomain,
                ingress.Hostnames);

    public static ResolvedStorageConfiguration? ToDomain(this ResourceDeploymentStorageConfiguration? storage) =>
        storage is null
            ? null
            : ResolvedStorageConfiguration.Create(
                storage.StorageProfileId,
                storage.StorageProfileRevision,
                storage.StorageClassName);

    public static ResourceDeploymentReconciliationSnapshot ToReconciliationSnapshot(this ResourceDeployment deployment)
    {
        ArgumentNullException.ThrowIfNull(deployment);

        return new ResourceDeploymentReconciliationSnapshot(
            deployment.Id,
            deployment.TenantId,
            ResourceTypeId.Create(deployment.ResourceType),
            deployment.ResourceId,
            deployment.ResourceGroupId,
            deployment.IsolationScopeId,
            deployment.RuntimeCellId,
            deployment.ResourceRevision,
            deployment.FixedReplicas,
            deployment.DesiredGeneration,
            deployment.UpdatedAtUtc,
            new ResourceDeploymentResolvedPlacementSnapshot(
                deployment.ResolvedPlacement.DeploymentTargetId,
                deployment.ResolvedPlacement.TargetWorkloadConfigurationRevision,
                deployment.ResolvedPlacement.KubernetesNamespace,
                deployment.ResolvedPlacement.Ingress is null
                    ? null
                    : new ResourceDeploymentIngressConfiguration(
                        deployment.ResolvedPlacement.Ingress.IngressClassName,
                        deployment.ResolvedPlacement.Ingress.ClusterIssuerName,
                        deployment.ResolvedPlacement.Ingress.PublicBaseDomain,
                        deployment.ResolvedPlacement.Ingress.Hostnames),
                deployment.ResolvedPlacement.Storage is null
                    ? null
                    : new ResourceDeploymentStorageConfiguration(
                        deployment.ResolvedPlacement.Storage.StorageProfileId,
                        deployment.ResolvedPlacement.Storage.StorageProfileRevision,
                        deployment.ResolvedPlacement.Storage.StorageClassName),
                deployment.ResolvedPlacement.Compute.ToContract(deployment.ResolvedPlacement.DeploymentTargetId)));
    }

    public static ResourceDeploymentEndpoint ToDomain(this ResourceDeploymentEndpointDto endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return ResourceDeploymentEndpoint.Create(endpoint.Kind, endpoint.Host, endpoint.Port, endpoint.Scheme);
    }

    private static ResourceDeploymentComputeConfiguration
        ToContract(this ResolvedComputeConfiguration compute, string deploymentTargetId) => new(
        compute.ComputeProfileId,
        compute.ComputeProfileRevision,
        deploymentTargetId,
        compute.DisplayName,
        new ResourceDeploymentComputeResourceValues(compute.Requests.CpuMillis, compute.Requests.MemoryMi),
        new ResourceDeploymentComputeResourceValues(compute.Limits.CpuMillis, compute.Limits.MemoryMi),
        compute.Architecture,
        compute.ExtendedResources
            .Select(resource => new ResourceDeploymentExtendedResource(resource.Name, resource.Quantity))
            .ToImmutableList(),
        compute.NodeSelector
            .Select(selector => new ResourceDeploymentNodeSelector(selector.Key, selector.Value))
            .ToImmutableList(),
        compute.RequiredNodeAffinity.Select(ToContract).ToImmutableList(),
        compute.PreferredNodeAffinity
            .Select(term => new ResourceDeploymentPreferredNodeAffinityTerm(term.Weight, ToContract(term.Term)))
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

    private static ResolvedComputeNodeAffinityTerm ToDomain(ResourceDeploymentNodeAffinityTerm term) =>
        ResolvedComputeNodeAffinityTerm.Create(
            term.Expressions.Select(expression =>
                ResolvedComputeNodeAffinityExpression.Create(expression.Key, expression.Operator, expression.Values)));

    private static ResourceDeploymentNodeAffinityTerm ToContract(ResolvedComputeNodeAffinityTerm term) => new(
        term.Expressions
            .Select(expression => new ResourceDeploymentNodeAffinityExpression(
                expression.Key,
                expression.Operator,
                expression.Values))
            .ToImmutableList());
}
