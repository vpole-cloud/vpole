using System.Collections.Immutable;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Common;

public static class ResourceDeploymentDtoExtensions
{
    public static ResourceDeploymentDto ToDto(this ResourceDeployment deployment,
        ResourceDeploymentIsolationDto isolation) =>
        new(
            deployment.Id,
            deployment.ResourceType,
            deployment.ResourceId,
            deployment.ResourceGroupId,
            deployment.ToPlacementSummaryDto(isolation),
            deployment.CreatedAtUtc,
            deployment.UpdatedAtUtc);

    public static PlacementSummaryDto ToPlacementSummaryDto(
        this ResourceDeployment deployment,
        ResourceDeploymentIsolationDto isolation) =>
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
            deployment.Endpoints.Select(ToDto).ToImmutableList(),
            deployment.ResourceRevision,
            deployment.FixedReplicas,
            deployment.ResolvedPlacement.Ingress is null
                ? null
                : new ResourceDeploymentIngressConfiguration(
                    deployment.ResolvedPlacement.Ingress.IngressClassName,
                    deployment.ResolvedPlacement.Ingress.ClusterIssuerName,
                    deployment.ResolvedPlacement.Ingress.PublicBaseDomain,
                    deployment.ResolvedPlacement.Ingress.Hostnames),
            deployment.Revision,
            deployment.DesiredOperation.ToContractOperation(),
            isolation);

    public static ResourceDeploymentDesiredOperation ToContractOperation(this ResourceDeploymentOperation operation) =>
        operation switch
        {
            ResourceDeploymentOperation.Apply => ResourceDeploymentDesiredOperation.Apply,
            ResourceDeploymentOperation.Delete => ResourceDeploymentDesiredOperation.Delete,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

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
            endpoint.Kind,
            endpoint.Host,
            endpoint.Port,
            endpoint.Scheme);

    private static ResourceDeploymentStatus Status(ResourceDeployment deployment) =>
        deployment.State switch
        {
            PreparedResourceDeploymentState => ResourceDeploymentStatus.Prepared,
            WaitingForRuntimeCellResourceDeploymentState =>
                ResourceDeploymentStatus.WaitingForRuntimeCell,
            PendingResourceDeploymentState
            {
                Operation: ResourceDeploymentOperation.Apply
            } => ResourceDeploymentStatus.Pending,
            PendingResourceDeploymentState
            {
                Operation: ResourceDeploymentOperation.Delete
            } => ResourceDeploymentStatus.Deleting,
            ReconcilingResourceDeploymentState
            {
                Operation: ResourceDeploymentOperation.Apply
            } => ResourceDeploymentStatus.Reconciling,
            ReconcilingResourceDeploymentState
            {
                Operation: ResourceDeploymentOperation.Delete
            } => ResourceDeploymentStatus.Deleting,
            AppliedResourceDeploymentState => ResourceDeploymentStatus.Applied,
            FailedResourceDeploymentState
            {
                Operation: ResourceDeploymentOperation.Delete
            } => ResourceDeploymentStatus.DeleteFailed,
            FailedResourceDeploymentState => ResourceDeploymentStatus.Failed,
            DeletedResourceDeploymentState => ResourceDeploymentStatus.Deleted,
            _ => throw new InvalidOperationException(
                $"Unsupported deployment state '{deployment.State.GetType().Name}'.")
        };
}
