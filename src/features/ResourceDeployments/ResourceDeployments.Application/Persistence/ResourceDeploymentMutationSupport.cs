using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Persistence;

internal union LoadedResourceDeploymentOutcome(
    ResourceDeployment,
    ResourceDeploymentMutationError);

internal static class ResourceDeploymentMutationSupport
{
    public static async Task<LoadedResourceDeploymentOutcome>
        LoadSingleAsync(
            IMongoCollection<ResourceDeployment> resourceDeployments,
            ResourceDeploymentMutationRequest request,
            IClientSessionHandle session,
            CancellationToken cancellationToken)
    {
        EnsureResourceType(request.ResourceType, nameof(request));
        var deployments = await resourceDeployments
            .Find(
                session,
                ResourceDeploymentFilters.ForResource(
                    request.TenantId,
                    request.ResourceType,
                    request.ResourceId))
            .SortBy(deployment => deployment.Id)
            .Limit(2)
            .ToListAsync(cancellationToken);
        return deployments.Count switch
        {
            0 => new ResourceDeploymentMutationError(
                new ResourceDeploymentMutationNotFound(request.ResourceId)),
            1 => deployments[0],
            _ => new ResourceDeploymentMutationError(
                new ResourceDeploymentCardinalityConflict(request.ResourceId))
        };
    }

    public static async Task<LoadedResourceDeploymentOutcome>
        LoadExactAsync(
            IMongoCollection<ResourceDeployment> resourceDeployments,
            ResourceDeploymentExactMutationRequest request,
            IClientSessionHandle session,
            CancellationToken cancellationToken)
    {
        EnsureResourceType(request.ResourceType, nameof(request));
        var deployment = await resourceDeployments
            .Find(
                session,
                ResourceDeploymentFilters.ForExactDeployment(
                    request.TenantId,
                    request.ResourceType,
                    request.ResourceId,
                    request.ResourceDeploymentId))
            .FirstOrDefaultAsync(cancellationToken);
        return deployment is null
            ? new ResourceDeploymentMutationError(new ResourceDeploymentMutationNotFound(
                request.ResourceId,
                request.ResourceDeploymentId))
            : deployment;
    }

    public static ResourceDeploymentMutationError? EnsureExpectedCompute(
        string resourceId,
        ComputeProfileReference expectedCompute,
        ResourceDeployment current) =>
        current.ResolvedPlacement.Compute.Matches(
            expectedCompute.ComputeProfileId,
            expectedCompute.ComputeProfileRevision)
            ? (ResourceDeploymentMutationError?)null
            : new ResourceDeploymentMutationError(
                new ResourceDeploymentComputeConfigurationChanged(
                    [expectedCompute.ComputeProfileId]));

    public static bool HasSameIngressIdentity(
        ResolvedIngressConfiguration? current,
        ResourceDeploymentIngressConfiguration replacement) =>
        current is not null &&
        string.Equals(
            current.IngressClassName,
            replacement.IngressClassName,
            StringComparison.Ordinal) &&
        string.Equals(
            current.ClusterIssuerName,
            replacement.ClusterIssuerName,
            StringComparison.Ordinal) &&
        string.Equals(
            current.PublicBaseDomain,
            replacement.PublicBaseDomain,
            StringComparison.Ordinal);

    public static bool Matches(
        ResourceDeploymentRequestedOperation expected,
        ResourceDeploymentOperation current) =>
        (expected, current) switch
        {
            (ResourceDeploymentRequestedOperation.Apply, ResourceDeploymentOperation.Apply) => true,
            (ResourceDeploymentRequestedOperation.Delete, ResourceDeploymentOperation.Delete) => true,
            _ => false
        };

    public static async Task<ResourceDeploymentMutationOutcome>
        ReplaceAndEnqueueAsync(
            IMongoCollection<ResourceDeployment> resourceDeployments,
            StoreOutboxWrites storeOutboxWrites,
            ResourceDeployment current,
            ResourceDeployment updated,
            IClientSessionHandle session,
            CancellationToken cancellationToken)
    {
        var replacement = await resourceDeployments.ReplaceOneAsync(
            session,
            ResourceDeploymentFilters.AtRevision(current),
            updated,
            cancellationToken: cancellationToken);
        if (replacement.ModifiedCount != 1)
        {
            return new ResourceDeploymentMutationError(new ResourceDeploymentChanged(
                current.ResourceId,
                current.Id));
        }

        if (updated.State is PendingResourceDeploymentState)
        {
            await storeOutboxWrites(
                session,
                [updated.ToCurrentRequestWrite()],
                cancellationToken);
        }

        return updated.ToPlacementSummaryDto();
    }

    public static void EnsureSetIsNonEmptyAndUnique<T>(
        IReadOnlyList<T> deployments,
        Func<T, string> id,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(deployments);
        if (deployments.Any(static deployment => deployment is null))
            throw new ArgumentException("A deployment set cannot contain null entries.", parameterName);
        if (deployments.Count == 0)
            throw new ArgumentException("At least one resource deployment is required.", parameterName);
        if (deployments.Select(id).Distinct(StringComparer.Ordinal).Count() != deployments.Count)
        {
            throw new ArgumentException(
                "Resource deployment IDs must be unique.",
                parameterName);
        }
    }

    private static void EnsureResourceType(ResourceTypeId resourceType, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(resourceType.Value))
            throw new ArgumentException("Resource type is required.", parameterName);
    }
}
