using MongoDB.Driver;
using System.Collections.Immutable;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Reads;

internal sealed class MongoLoadResourceDeploymentRuntimePlacement(
    IMongoCollection<ResourceDeployment> resourceDeployments)
{
    public async Task<ResourceDeploymentRuntimePlacementDto?> LoadExactAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string deploymentId,
        CancellationToken cancellationToken = default)
    {
        var deployment = await resourceDeployments
            .Find(ResourceDeploymentFilters.ForExactDeployment(
                tenantId,
                resourceType,
                resourceId,
                deploymentId))
            .FirstOrDefaultAsync(cancellationToken);
        return deployment is null ? null : ToRuntimePlacement(deployment);
    }

    public async Task<IReadOnlyList<string>> LoadTargetIdsAsync(
        ResourceTypeId resourceType,
        CancellationToken cancellationToken = default)
    {
        var deployments = await resourceDeployments
            .Find(ResourceDeploymentFilters.ForResourceType(resourceType))
            .Project(deployment => deployment.ResolvedPlacement.DeploymentTargetId)
            .ToListAsync(cancellationToken);
        return deployments
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static ResourceDeploymentRuntimePlacementDto ToRuntimePlacement(
        ResourceDeployment deployment) =>
        new(
            deployment.Id,
            deployment.ResourceId,
            deployment.RuntimeCellId,
            deployment.ResolvedPlacement.DeploymentTargetId,
            deployment.ResolvedPlacement.KubernetesNamespace,
            deployment.Revision,
            ToContract(
                deployment.ResolvedPlacement.Compute,
                deployment.ResolvedPlacement.DeploymentTargetId),
            deployment.State switch
            {
                WaitingForRuntimeCellResourceDeploymentState => "WaitingForRuntimeCell",
                PendingResourceDeploymentState => "Pending",
                ReconcilingResourceDeploymentState => "Reconciling",
                AppliedResourceDeploymentState => "Applied",
                FailedResourceDeploymentState => "Failed",
                DeletedResourceDeploymentState => "Deleted",
                _ => throw new InvalidOperationException(
                    $"Unsupported resource deployment state '{deployment.State.GetType().FullName}'.")
            },
            deployment.DesiredOperation.ToString());

    private static ResourceDeploymentComputeConfiguration ToContract(
        ResolvedComputeConfiguration compute,
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
            compute.RequiredNodeAffinity
                .Select(ToContract)
                .ToImmutableList(),
            compute.PreferredNodeAffinity
                .Select(term => new ResourceDeploymentPreferredNodeAffinityTerm(
                    term.Weight,
                    ToContract(term.Term)))
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

    private static ResourceDeploymentNodeAffinityTerm ToContract(
        ResolvedComputeNodeAffinityTerm term) =>
        new(term.Expressions
            .Select(expression => new ResourceDeploymentNodeAffinityExpression(
                expression.Key,
                expression.Operator,
                expression.Values))
            .ToImmutableList());
}
