using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Persistence;

public static class ResourceDeploymentFilters
{
    public static FilterDefinition<ResourceDeployment> VisibleToTenant(
        string tenantId) =>
        Builders<ResourceDeployment>.Filter.ForTenant(tenantId);

    public static FilterDefinition<ResourceDeployment> ForResource(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId) =>
        Builders<ResourceDeployment>.Filter.And(
            VisibleToTenant(tenantId),
            Builders<ResourceDeployment>.Filter.Eq(
                deployment => deployment.ResourceType,
                resourceType.Value),
            Builders<ResourceDeployment>.Filter.Eq(
                deployment => deployment.ResourceId,
                resourceId));

    public static FilterDefinition<ResourceDeployment> ForResources(
        string tenantId,
        ResourceTypeId resourceType,
        IReadOnlyCollection<string> resourceIds)
    {
        ArgumentNullException.ThrowIfNull(resourceIds);
        return Builders<ResourceDeployment>.Filter.And(
            VisibleToTenant(tenantId),
            Builders<ResourceDeployment>.Filter.Eq(
                deployment => deployment.ResourceType,
                resourceType.Value),
            Builders<ResourceDeployment>.Filter.In(
                deployment => deployment.ResourceId,
                resourceIds));
    }

    public static FilterDefinition<ResourceDeployment> AtRevision(
        ResourceDeployment deployment) =>
        Builders<ResourceDeployment>.Filter.And(
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
