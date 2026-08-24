using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.Persistence;

internal static class IsolationScopeFilters
{
    public static FilterDefinition<IsolationScope> ByTenantAndResourceGroup(
        string tenantId,
        string resourceGroupId) =>
        Builders<IsolationScope>.Filter.And(
            Builders<IsolationScope>.Filter.ForTenant(tenantId),
            Builders<IsolationScope>.Filter.Eq(scope => scope.ResourceGroupId, resourceGroupId));

    public static FilterDefinition<IsolationScope> ByTenantResourceGroupAndId(
        string tenantId,
        string resourceGroupId,
        string isolationScopeId) =>
        Builders<IsolationScope>.Filter.And(
            ByTenantAndResourceGroup(tenantId, resourceGroupId),
            Builders<IsolationScope>.Filter.Eq(scope => scope.Id, isolationScopeId));

    public static FilterDefinition<IsolationScope> ByTenantResourceGroupAndNormalizedName(
        string tenantId,
        string resourceGroupId,
        string normalizedName) =>
        Builders<IsolationScope>.Filter.And(
            ByTenantAndResourceGroup(tenantId, resourceGroupId),
            Builders<IsolationScope>.Filter.Eq(scope => scope.NormalizedName, normalizedName));

    public static FilterDefinition<IsolationScope> ActiveAtRevision(
        string tenantId,
        string resourceGroupId,
        string isolationScopeId,
        long revision) =>
        Builders<IsolationScope>.Filter.And(
            ByTenantResourceGroupAndId(tenantId, resourceGroupId, isolationScopeId),
            Builders<IsolationScope>.Filter.Eq(scope => scope.Revision, revision),
            Builders<IsolationScope>.Filter
                .OfType<IsolationScopeLifecycleState, ActiveIsolationScopeLifecycleState>(
                    scope => scope.Lifecycle));

    public static FilterDefinition<IsolationScope> AtRevision(
        string tenantId,
        string resourceGroupId,
        string isolationScopeId,
        long revision) =>
        Builders<IsolationScope>.Filter.And(
            ByTenantResourceGroupAndId(tenantId, resourceGroupId, isolationScopeId),
            Builders<IsolationScope>.Filter.Eq(scope => scope.Revision, revision));

    public static FilterDefinition<IsolationScope> Deleting() =>
        Builders<IsolationScope>.Filter
            .OfType<IsolationScopeLifecycleState, DeletingIsolationScopeLifecycleState>(
                scope => scope.Lifecycle);
}
