using System.Collections.Immutable;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Common;

internal static class ResourceDeploymentContractMappings
{
    public static ResolvedComputeConfiguration ToDomain(
        this ResourceDeploymentComputeConfiguration compute)
    {
        ArgumentNullException.ThrowIfNull(compute);

        return ResolvedComputeConfiguration.Create(
            compute.ComputeProfileId,
            compute.ComputeProfileRevision,
            compute.DisplayName,
            ResolvedComputeResourceValues.Create(
                compute.Requests.CpuMillis,
                compute.Requests.MemoryMi),
            ResolvedComputeResourceValues.Create(
                compute.Limits.CpuMillis,
                compute.Limits.MemoryMi),
            compute.Architecture switch
            {
                ResourceDeploymentComputeArchitecture.Any => ResolvedComputeArchitecture.Any,
                ResourceDeploymentComputeArchitecture.Amd64 => ResolvedComputeArchitecture.Amd64,
                ResourceDeploymentComputeArchitecture.Arm64 => ResolvedComputeArchitecture.Arm64,
                _ => throw new ArgumentOutOfRangeException(nameof(compute))
            },
            compute.ExtendedResources.Select(resource =>
                ResolvedComputeExtendedResource.Create(resource.Name, resource.Quantity)),
            compute.NodeSelector.Select(selector =>
                ResolvedComputeNodeSelector.Create(selector.Key, selector.Value)),
            compute.RequiredNodeAffinity.Select(ToDomain),
            compute.PreferredNodeAffinity.Select(term =>
                ResolvedComputePreferredNodeAffinityTerm.Create(
                    term.Weight,
                    ToDomain(term.Term))),
            compute.Tolerations.Select(toleration =>
                ResolvedComputeToleration.Create(
                    toleration.Key,
                    ToDomain(toleration.Operator),
                    toleration.Value,
                    ToDomain(toleration.Effect),
                    toleration.TolerationSeconds)),
            compute.RuntimeClassName);
    }

    public static ResolvedIngressConfiguration? ToDomain(
        this ResourceDeploymentIngressConfiguration? ingress) =>
        ingress is null
            ? null
            : ResolvedIngressConfiguration.Create(
                ingress.IngressClassName,
                ingress.ClusterIssuerName);

    public static ResolvedStorageConfiguration? ToDomain(
        this ResourceDeploymentStorageConfiguration? storage) =>
        storage is null
            ? null
            : ResolvedStorageConfiguration.Create(
                storage.StorageProfileId,
                storage.StorageProfileRevision,
                storage.StorageClassName);

    public static ResourceDeploymentReconciliationSnapshot ToReconciliationSnapshot(
        this ResourceDeployment deployment)
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
            deployment.DesiredGeneration,
            deployment.UpdatedAtUtc,
            new ResourceDeploymentResolvedPlacementSnapshot(
                deployment.ResolvedPlacement.DeploymentTargetId,
                deployment.ResolvedPlacement.KubernetesNamespace,
                deployment.ResolvedPlacement.Ingress is null
                    ? null
                    : new ResourceDeploymentIngressConfiguration(
                        deployment.ResolvedPlacement.Ingress.IngressClassName,
                        deployment.ResolvedPlacement.Ingress.ClusterIssuerName),
                deployment.ResolvedPlacement.Storage is null
                    ? null
                    : new ResourceDeploymentStorageConfiguration(
                        deployment.ResolvedPlacement.Storage.StorageProfileId,
                        deployment.ResolvedPlacement.Storage.StorageProfileRevision,
                        deployment.ResolvedPlacement.Storage.StorageClassName),
                deployment.ResolvedPlacement.Compute.ToContract(
                    deployment.ResolvedPlacement.DeploymentTargetId)));
    }

    public static ResourceDeploymentEndpoint ToDomain(
        this ResourceDeploymentEndpointDto endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return endpoint.Kind switch
        {
            "Ingress" => IngressResourceDeploymentEndpoint.Create(
                endpoint.Host,
                endpoint.Port,
                endpoint.Scheme),
            "Redis" => RedisResourceDeploymentEndpoint.Create(
                endpoint.Host,
                endpoint.Port),
            "PostgreSql" => PostgreSqlResourceDeploymentEndpoint.Create(
                endpoint.Host,
                endpoint.Port),
            _ => throw new InvalidOperationException(
                $"Unsupported resource deployment endpoint kind '{endpoint.Kind}'.")
        };
    }

    private static ResourceDeploymentComputeConfiguration ToContract(
        this ResolvedComputeConfiguration compute,
        string deploymentTargetId) =>
        new(
            compute.ComputeProfileId,
            compute.ComputeProfileRevision,
            deploymentTargetId,
            compute.DisplayName,
            new ResourceDeploymentComputeResourceValues(
                compute.Requests.CpuMillis,
                compute.Requests.MemoryMi),
            new ResourceDeploymentComputeResourceValues(
                compute.Limits.CpuMillis,
                compute.Limits.MemoryMi),
            compute.Architecture switch
            {
                ResolvedComputeArchitecture.Any => ResourceDeploymentComputeArchitecture.Any,
                ResolvedComputeArchitecture.Amd64 => ResourceDeploymentComputeArchitecture.Amd64,
                ResolvedComputeArchitecture.Arm64 => ResourceDeploymentComputeArchitecture.Arm64,
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
            compute.RequiredNodeAffinity.Select(ToContract).ToImmutableList(),
            compute.PreferredNodeAffinity
                .Select(term => new ResourceDeploymentPreferredNodeAffinityTerm(
                    term.Weight,
                    ToContract(term.Term)))
                .ToImmutableList(),
            compute.Tolerations
                .Select(toleration => new ResourceDeploymentToleration(
                    toleration.Key,
                    ToContract(toleration.Operator),
                    toleration.Value,
                    ToContract(toleration.Effect),
                    toleration.TolerationSeconds))
                .ToImmutableList(),
            compute.RuntimeClassName);

    private static ResolvedComputeNodeAffinityTerm ToDomain(
        ResourceDeploymentNodeAffinityTerm term) =>
        ResolvedComputeNodeAffinityTerm.Create(
            term.Expressions.Select(expression =>
                ResolvedComputeNodeAffinityExpression.Create(
                    expression.Key,
                    ToDomain(expression.Operator),
                    expression.Values)));

    private static ResourceDeploymentNodeAffinityTerm ToContract(
        ResolvedComputeNodeAffinityTerm term) =>
        new(term.Expressions
            .Select(expression => new ResourceDeploymentNodeAffinityExpression(
                expression.Key,
                ToContract(expression.Operator),
                expression.Values))
            .ToImmutableList());

    private static ResolvedComputeNodeAffinityOperator ToDomain(
        ResourceDeploymentNodeAffinityOperator value) => value switch
        {
            ResourceDeploymentNodeAffinityOperator.In => ResolvedComputeNodeAffinityOperator.In,
            ResourceDeploymentNodeAffinityOperator.NotIn => ResolvedComputeNodeAffinityOperator.NotIn,
            ResourceDeploymentNodeAffinityOperator.Exists => ResolvedComputeNodeAffinityOperator.Exists,
            ResourceDeploymentNodeAffinityOperator.DoesNotExist => ResolvedComputeNodeAffinityOperator.DoesNotExist,
            ResourceDeploymentNodeAffinityOperator.GreaterThan => ResolvedComputeNodeAffinityOperator.GreaterThan,
            ResourceDeploymentNodeAffinityOperator.LessThan => ResolvedComputeNodeAffinityOperator.LessThan,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    private static ResourceDeploymentNodeAffinityOperator ToContract(
        ResolvedComputeNodeAffinityOperator value) => value switch
        {
            ResolvedComputeNodeAffinityOperator.In => ResourceDeploymentNodeAffinityOperator.In,
            ResolvedComputeNodeAffinityOperator.NotIn => ResourceDeploymentNodeAffinityOperator.NotIn,
            ResolvedComputeNodeAffinityOperator.Exists => ResourceDeploymentNodeAffinityOperator.Exists,
            ResolvedComputeNodeAffinityOperator.DoesNotExist => ResourceDeploymentNodeAffinityOperator.DoesNotExist,
            ResolvedComputeNodeAffinityOperator.GreaterThan => ResourceDeploymentNodeAffinityOperator.GreaterThan,
            ResolvedComputeNodeAffinityOperator.LessThan => ResourceDeploymentNodeAffinityOperator.LessThan,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    private static ResolvedComputeTolerationOperator ToDomain(
        ResourceDeploymentTolerationOperator value) => value switch
        {
            ResourceDeploymentTolerationOperator.Equal => ResolvedComputeTolerationOperator.Equal,
            ResourceDeploymentTolerationOperator.Exists => ResolvedComputeTolerationOperator.Exists,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    private static ResourceDeploymentTolerationOperator ToContract(
        ResolvedComputeTolerationOperator value) => value switch
        {
            ResolvedComputeTolerationOperator.Equal => ResourceDeploymentTolerationOperator.Equal,
            ResolvedComputeTolerationOperator.Exists => ResourceDeploymentTolerationOperator.Exists,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    private static ResolvedComputeTaintEffect ToDomain(
        ResourceDeploymentTaintEffect value) => value switch
        {
            ResourceDeploymentTaintEffect.Any => ResolvedComputeTaintEffect.Any,
            ResourceDeploymentTaintEffect.NoSchedule => ResolvedComputeTaintEffect.NoSchedule,
            ResourceDeploymentTaintEffect.PreferNoSchedule => ResolvedComputeTaintEffect.PreferNoSchedule,
            ResourceDeploymentTaintEffect.NoExecute => ResolvedComputeTaintEffect.NoExecute,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    private static ResourceDeploymentTaintEffect ToContract(
        ResolvedComputeTaintEffect value) => value switch
        {
            ResolvedComputeTaintEffect.Any => ResourceDeploymentTaintEffect.Any,
            ResolvedComputeTaintEffect.NoSchedule => ResourceDeploymentTaintEffect.NoSchedule,
            ResolvedComputeTaintEffect.PreferNoSchedule => ResourceDeploymentTaintEffect.PreferNoSchedule,
            ResolvedComputeTaintEffect.NoExecute => ResourceDeploymentTaintEffect.NoExecute,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
}
