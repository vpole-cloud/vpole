using System.Collections.Immutable;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Common;

public static class ResourceDeploymentDtoExtensions
{
    public static ResourceDeploymentDto ToDto(this ResourceDeployment deployment) =>
        new(
            deployment.Id,
            deployment.ResourceType,
            deployment.ResourceId,
            deployment.ResourceGroupId,
            deployment.ToPlacementSummaryDto(),
            deployment.CreatedAtUtc,
            deployment.UpdatedAtUtc);

    public static PlacementSummaryDto ToPlacementSummaryDto(
        this ResourceDeployment deployment) =>
        new(
            deployment.Id,
            deployment.IsolationScopeId,
            deployment.RuntimeCellId,
            deployment.ResolvedPlacement.DeploymentTargetId,
            deployment.DesiredGeneration,
            deployment.ObservedGeneration,
            Status(deployment),
            deployment.State is FailedResourceDeploymentState failed
                ? failed.FailureCode
                : null,
            deployment.ResolvedPlacement.Compute.ToSummaryDto(),
            deployment.Endpoints.Select(ToDto).ToImmutableList());

    public static ComputeConfigurationSummaryDto ToSummaryDto(
        this ResolvedComputeConfiguration compute) =>
        new(
            new ComputeProfileReference(
                compute.ComputeProfileId,
                compute.ComputeProfileRevision),
            compute.DisplayName,
            new ComputeResourceValuesDto(
                compute.Requests.CpuMillis,
                compute.Requests.MemoryMi),
            new ComputeResourceValuesDto(
                compute.Limits.CpuMillis,
                compute.Limits.MemoryMi),
            compute.ExtendedResources
                .Select(resource => new ComputeExtendedResourceDto(
                    resource.Name,
                    resource.Quantity))
                .ToImmutableList());

    private static ResourceDeploymentEndpointDto ToDto(
        ResourceDeploymentEndpoint endpoint) =>
        new(
            endpoint switch
            {
                IngressResourceDeploymentEndpoint => "Ingress",
                RedisResourceDeploymentEndpoint => "Redis",
                PostgreSqlResourceDeploymentEndpoint => "PostgreSql",
                _ => throw new InvalidOperationException(
                    $"Unsupported deployment endpoint '{endpoint.GetType().Name}'.")
            },
            endpoint.Host,
            endpoint.Port,
            endpoint.Scheme);

    private static string Status(ResourceDeployment deployment) =>
        deployment.State switch
        {
            WaitingForRuntimeCellResourceDeploymentState =>
                "WaitingForRuntimeCell",
            PendingResourceDeploymentState
                {
                    Operation: ResourceDeploymentOperation.Apply
                } => "Pending",
            PendingResourceDeploymentState
                {
                    Operation: ResourceDeploymentOperation.Delete
                } => "Deleting",
            ReconcilingResourceDeploymentState
                {
                    Operation: ResourceDeploymentOperation.Apply
                } => "Reconciling",
            ReconcilingResourceDeploymentState
                {
                    Operation: ResourceDeploymentOperation.Delete
                } => "Deleting",
            AppliedResourceDeploymentState => "Applied",
            FailedResourceDeploymentState
                {
                    Operation: ResourceDeploymentOperation.Delete
                } => "DeleteFailed",
            FailedResourceDeploymentState => "Failed",
            DeletedResourceDeploymentState => "Deleted",
            _ => throw new InvalidOperationException(
                $"Unsupported deployment state '{deployment.State.GetType().Name}'.")
        };
}
