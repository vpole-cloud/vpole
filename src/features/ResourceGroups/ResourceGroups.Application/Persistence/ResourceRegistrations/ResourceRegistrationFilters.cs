using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceGroups.Domain;
using Pole.Workloads;

namespace Pole.Features.ResourceGroups.Application.Persistence.ResourceRegistrations;

internal static class ResourceRegistrationFilters
{
    public static FilterDefinition<ResourceRegistration> ByIdentity(
        string tenantId,
        ResourceTypeId resourceTypeId,
        string resourceId) =>
        Builders<ResourceRegistration>.Filter.And(
            Builders<ResourceRegistration>.Filter.ForTenant(tenantId),
            Builders<ResourceRegistration>.Filter.Eq(
                registration => registration.ResourceTypeId,
                resourceTypeId.Value),
            Builders<ResourceRegistration>.Filter.Eq(
                registration => registration.ResourceId,
                resourceId));

    public static FilterDefinition<ResourceRegistration> Exact(
        string tenantId,
        string resourceGroupId,
        ResourceTypeId resourceTypeId,
        string resourceId) =>
        Builders<ResourceRegistration>.Filter.And(
            ByIdentity(tenantId, resourceTypeId, resourceId),
            Builders<ResourceRegistration>.Filter.Eq(
                registration => registration.ResourceGroupId,
                resourceGroupId));

    public static FilterDefinition<ResourceRegistration> ByResourceGroup(
        string tenantId,
        string resourceGroupId) =>
        Builders<ResourceRegistration>.Filter.And(
            Builders<ResourceRegistration>.Filter.ForTenant(tenantId),
            Builders<ResourceRegistration>.Filter.Eq(
                registration => registration.ResourceGroupId,
                resourceGroupId));

    public static FilterDefinition<ResourceRegistration> ByResourceGroups(
        string tenantId,
        IReadOnlyCollection<string> resourceGroupIds) =>
        Builders<ResourceRegistration>.Filter.And(
            Builders<ResourceRegistration>.Filter.ForTenant(tenantId),
            Builders<ResourceRegistration>.Filter.In(
                registration => registration.ResourceGroupId,
                resourceGroupIds));
}
