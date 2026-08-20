using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Persistence;

internal static class ResourceDeploymentFilters
{
    public static FilterDefinition<ResourceDeployment> VisibleToTenant(
        string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return Builders<ResourceDeployment>.Filter.ForTenant(tenantId);
    }

    public static FilterDefinition<ResourceDeployment> ForResource(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId)
    {
        EnsureResourceType(resourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        return Builders<ResourceDeployment>.Filter.And(
            VisibleToTenant(tenantId),
            Builders<ResourceDeployment>.Filter.Eq(
                deployment => deployment.ResourceType,
                resourceType.Value),
            Builders<ResourceDeployment>.Filter.Eq(
                deployment => deployment.ResourceId,
                resourceId));
    }

    public static FilterDefinition<ResourceDeployment> ForResources(
        string tenantId,
        ResourceTypeId resourceType,
        IReadOnlyCollection<string> resourceIds)
    {
        ArgumentNullException.ThrowIfNull(resourceIds);
        EnsureResourceType(resourceType);
        if (resourceIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Resource IDs cannot contain null or whitespace values.",
                nameof(resourceIds));
        }

        return Builders<ResourceDeployment>.Filter.And(
            VisibleToTenant(tenantId),
            Builders<ResourceDeployment>.Filter.Eq(
                deployment => deployment.ResourceType,
                resourceType.Value),
            Builders<ResourceDeployment>.Filter.In(
                deployment => deployment.ResourceId,
                resourceIds));
    }

    public static FilterDefinition<ResourceDeployment> ForExactDeployment(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string deploymentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);
        return Builders<ResourceDeployment>.Filter.And(
            ForResource(tenantId, resourceType, resourceId),
            Builders<ResourceDeployment>.Filter.Eq(
                deployment => deployment.Id,
                deploymentId));
    }

    public static FilterDefinition<ResourceDeployment> AtRevision(
        ResourceDeployment deployment)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        return Builders<ResourceDeployment>.Filter.And(
            ForResource(
                deployment.TenantId,
                ResourceTypeId.Create(deployment.ResourceType),
                deployment.ResourceId),
            Builders<ResourceDeployment>.Filter.Eq(
                item => item.Id,
                deployment.Id),
            Builders<ResourceDeployment>.Filter.Eq(
                item => item.Revision,
                deployment.Revision));
    }

    public static FilterDefinition<ResourceDeployment> ReferencesComputeProfile(
        string computeProfileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(computeProfileId);
        return Builders<ResourceDeployment>.Filter.Eq(
            deployment => deployment.ResolvedPlacement.Compute.ComputeProfileId,
            computeProfileId);
    }

    private static void EnsureResourceType(ResourceTypeId resourceType)
    {
        if (string.IsNullOrWhiteSpace(resourceType.Value))
            throw new ArgumentException("Resource type is required.", nameof(resourceType));
    }
}
