using MongoDB.Driver;
using MongoDB.Bson;
using Pole.Database;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.Persistence;

internal static class ResourceGroupFilters
{
    public static FilterDefinition<ResourceGroup> ByTechnicalName(string tenantId, string name) =>
        Builders<ResourceGroup>.Filter.ForTenant(tenantId) & Builders<ResourceGroup>.Filter.Regex(item => item.Name,
            new MongoDB.Bson.BsonRegularExpression("^" + System.Text.RegularExpressions.Regex.Escape(name) + "$", "i"));

    public static FilterDefinition<ResourceGroup> ByTenantAndId(
        string tenantId,
        string resourceGroupId) =>
        Builders<ResourceGroup>.Filter.And(
            Builders<ResourceGroup>.Filter.ForTenant(tenantId),
            Builders<ResourceGroup>.Filter.Eq(group => group.Id, resourceGroupId));

    public static FilterDefinition<ResourceGroup> AtRevision(
        string tenantId,
        string resourceGroupId,
        long revision) =>
        Builders<ResourceGroup>.Filter.And(
            ByTenantAndId(tenantId, resourceGroupId),
            Builders<ResourceGroup>.Filter.Eq(group => group.Revision, revision));

    public static FilterDefinition<ResourceGroup> AtRevisionAndRegistrationFence(
        ResourceGroup resourceGroup) =>
        Builders<ResourceGroup>.Filter.And(
            AtRevision(
                resourceGroup.TenantId,
                resourceGroup.Id,
                resourceGroup.Revision),
            Builders<ResourceGroup>.Filter.Eq(
                group => group.RegistrationFence,
                resourceGroup.RegistrationFence));

    public static FilterDefinition<ResourceGroup> ActiveAtRevision(
        string tenantId,
        string resourceGroupId,
        long revision) =>
        Builders<ResourceGroup>.Filter.And(
            AtRevision(tenantId, resourceGroupId, revision),
            Builders<ResourceGroup>.Filter
                .OfType<ResourceGroupLifecycleState, ActiveResourceGroupLifecycleState>(
                    group => group.Lifecycle));

    public static FilterDefinition<ResourceGroup> ActiveAtRevisionAndRegistrationFence(
        ResourceGroup resourceGroup) =>
        Builders<ResourceGroup>.Filter.And(
            AtRevisionAndRegistrationFence(resourceGroup),
            Builders<ResourceGroup>.Filter
                .OfType<ResourceGroupLifecycleState, ActiveResourceGroupLifecycleState>(
                    group => group.Lifecycle));

    public static FilterDefinition<ResourceGroup> Active(
        string tenantId,
        string resourceGroupId) =>
        Builders<ResourceGroup>.Filter.And(
            ByTenantAndId(tenantId, resourceGroupId),
            Builders<ResourceGroup>.Filter
                .OfType<ResourceGroupLifecycleState, ActiveResourceGroupLifecycleState>(
                    group => group.Lifecycle));

    public static FilterDefinition<ResourceGroup> Deleting(
        string tenantId,
        string resourceGroupId) =>
        Builders<ResourceGroup>.Filter.And(
            ByTenantAndId(tenantId, resourceGroupId),
            Builders<ResourceGroup>.Filter
                .OfType<ResourceGroupLifecycleState, DeletingResourceGroupLifecycleState>(
                    group => group.Lifecycle));

    public static FilterDefinition<ResourceGroup> Search(
        string tenantId,
        string? search) =>
        string.IsNullOrWhiteSpace(search)
            ? Builders<ResourceGroup>.Filter.ForTenant(tenantId)
            : Builders<ResourceGroup>.Filter.And(
                Builders<ResourceGroup>.Filter.ForTenant(tenantId),
                Builders<ResourceGroup>.Filter.Regex(
                    group => group.Name,
                    new BsonRegularExpression(
                        $".*{System.Text.RegularExpressions.Regex.Escape(search)}.*",
                        "i")));
}
