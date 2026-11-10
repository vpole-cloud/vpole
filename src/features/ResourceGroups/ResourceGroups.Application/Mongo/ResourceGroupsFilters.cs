using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.IsolationScopes;
using Pole.Features.ResourceGroups.Application.References;
using Pole.Features.ResourceGroups.Application.ResourceRegistrations;
using Pole.Features.ResourceGroups.Application.RuntimeCells;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Workloads;

namespace Pole.Features.ResourceGroups.Application.Mongo;

internal static class IsolationScopeFilters
{
    public static FilterDefinition<IsolationScope> ByTenantAndId(string tenantId, string id) =>
        Builders<IsolationScope>.Filter.ForTenant(tenantId) &
        Builders<IsolationScope>.Filter.Eq(scope => scope.Id, id);

    public static FilterDefinition<IsolationScope> ByTechnicalName(string tenantId, string name) =>
        Builders<IsolationScope>.Filter.ForTenant(tenantId) &
        Builders<IsolationScope>.Filter.Eq(scope => scope.NormalizedName, name) &
        Builders<IsolationScope>.Filter.Eq(scope => scope.IsDefault, false);

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

internal static class ResourceRegistrationFilters
{
    public static FilterDefinition<ResourceRegistration> AtPlanningSnapshot(
        ResourceRegistrationPlanningSnapshot snapshot) =>
        ByIdentity(snapshot.Resource.TenantId, snapshot.Resource.ResourceTypeId, snapshot.Resource.ResourceId) &
        Builders<ResourceRegistration>.Filter.Eq(item => item.Id, snapshot.Resource.RegistrationId) &
        Builders<ResourceRegistration>.Filter.Eq(item => item.ReferenceFence, snapshot.ReferenceFence);

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

internal static class RuntimeCellFilters
{
    public static FilterDefinition<RuntimeCell> ByTenantAndId(
        string tenantId,
        string runtimeCellId) =>
        Builders<RuntimeCell>.Filter.And(
            Builders<RuntimeCell>.Filter.ForTenant(tenantId),
            Builders<RuntimeCell>.Filter.Eq(cell => cell.Id, runtimeCellId));

    public static FilterDefinition<RuntimeCell> ById(string runtimeCellId) =>
        Builders<RuntimeCell>.Filter.Eq(cell => cell.Id, runtimeCellId);

    public static FilterDefinition<RuntimeCell> ByTenantAndIds(
        string tenantId,
        IReadOnlyCollection<string> runtimeCellIds) =>
        Builders<RuntimeCell>.Filter.And(
            Builders<RuntimeCell>.Filter.ForTenant(tenantId),
            Builders<RuntimeCell>.Filter.In(cell => cell.Id, runtimeCellIds));

    public static FilterDefinition<RuntimeCell> ByTenantResourceGroupAndId(
        string tenantId,
        string resourceGroupId,
        string runtimeCellId) =>
        Builders<RuntimeCell>.Filter.And(
            ByTenantAndId(tenantId, runtimeCellId),
            Builders<RuntimeCell>.Filter.Eq(cell => cell.ResourceGroupId, resourceGroupId));

    public static FilterDefinition<RuntimeCell> ByTenantAndResourceGroup(
        string tenantId,
        string resourceGroupId) =>
        Builders<RuntimeCell>.Filter.And(
            Builders<RuntimeCell>.Filter.ForTenant(tenantId),
            Builders<RuntimeCell>.Filter.Eq(cell => cell.ResourceGroupId, resourceGroupId));

    public static FilterDefinition<RuntimeCell> ByTenantResourceGroupAndScope(
        string tenantId,
        string resourceGroupId,
        string isolationScopeId) =>
        Builders<RuntimeCell>.Filter.And(
            ByTenantAndResourceGroup(tenantId, resourceGroupId),
            Builders<RuntimeCell>.Filter.Eq(cell => cell.IsolationScopeId, isolationScopeId));

    public static FilterDefinition<RuntimeCell> ByScopeAndDeploymentTarget(
        string isolationScopeId,
        string deploymentTargetId) =>
        Builders<RuntimeCell>.Filter.And(
            Builders<RuntimeCell>.Filter.Eq(cell => cell.IsolationScopeId, isolationScopeId),
            Builders<RuntimeCell>.Filter.Eq(cell => cell.DeploymentTargetId, deploymentTargetId));

    public static FilterDefinition<RuntimeCell> ByTenantScopeAndDeploymentTargets(
        string tenantId,
        string resourceGroupId,
        string isolationScopeId,
        IReadOnlyCollection<string> deploymentTargetIds) =>
        Builders<RuntimeCell>.Filter.And(
            ByTenantResourceGroupAndScope(tenantId, resourceGroupId, isolationScopeId),
            Builders<RuntimeCell>.Filter.In(cell => cell.DeploymentTargetId, deploymentTargetIds));

    public static FilterDefinition<RuntimeCell> AtDesiredGenerationAndState<TState>(
        RuntimeCell runtimeCell,
        long generation)
        where TState : RuntimeCellState =>
        Builders<RuntimeCell>.Filter.And(
            ByTenantAndId(runtimeCell.TenantId, runtimeCell.Id),
            Builders<RuntimeCell>.Filter.Eq(cell => cell.DesiredGeneration, generation),
            Builders<RuntimeCell>.Filter.OfType<RuntimeCellState, TState>(cell => cell.State));

    public static FilterDefinition<RuntimeCell> AtDeletionCheck(RuntimeCell runtimeCell)
    {
        var deleting = (DeletingRuntimeCellState)runtimeCell.State;
        return Builders<RuntimeCell>.Filter.And(
            AtDesiredGenerationAndState<DeletingRuntimeCellState>(runtimeCell, runtimeCell.DesiredGeneration),
            Builders<RuntimeCell>.Filter.Eq(cell => ((DeletingRuntimeCellState)cell.State).CheckSequence, deleting.CheckSequence),
            Builders<RuntimeCell>.Filter.Eq(cell => ((DeletingRuntimeCellState)cell.State).NextCheckAtUtc, deleting.NextCheckAtUtc));
    }

    public static FilterDefinition<RuntimeCell> AtDesiredGeneration(RuntimeCell runtimeCell) =>
        Builders<RuntimeCell>.Filter.And(
            ByTenantAndId(runtimeCell.TenantId, runtimeCell.Id),
            Builders<RuntimeCell>.Filter.Eq(
                cell => cell.DesiredGeneration,
                runtimeCell.DesiredGeneration));

    public static FilterDefinition<RuntimeCell> AtDesiredGenerationFailed(
        RuntimeCell runtimeCell) =>
        Builders<RuntimeCell>.Filter.And(
            ByTenantAndId(runtimeCell.TenantId, runtimeCell.Id),
            Builders<RuntimeCell>.Filter.Eq(
                cell => cell.DesiredGeneration,
                runtimeCell.DesiredGeneration),
            Builders<RuntimeCell>.Filter.OfType<RuntimeCellState, FailedRuntimeCellState>(
                cell => cell.State));

    public static FilterDefinition<RuntimeCell> NotDeleted(
        string tenantId,
        string resourceGroupId,
        string isolationScopeId) =>
        Builders<RuntimeCell>.Filter.And(
            ByTenantResourceGroupAndScope(tenantId, resourceGroupId, isolationScopeId),
            Builders<RuntimeCell>.Filter.Not(
                Builders<RuntimeCell>.Filter.OfType<RuntimeCellState, DeletedRuntimeCellState>(
                    cell => cell.State)));

    public static FilterDefinition<RuntimeCell> RecoveryCandidates(
        DateTime staleBefore,
        DateTime now) =>
        Builders<RuntimeCell>.Filter.Or(
            Builders<RuntimeCell>.Filter.And(
                Builders<RuntimeCell>.Filter.OfType<RuntimeCellState, ReadyRuntimeCellState>(cell => cell.State),
                Builders<RuntimeCell>.Filter.Lte(
                    cell => cell.UpdatedAtUtc,
                    RuntimeCellVerificationSchedule.DueBefore(now))),
            Builders<RuntimeCell>.Filter.And(
                Builders<RuntimeCell>.Filter
                    .OfType<RuntimeCellState, ProvisioningRuntimeCellState>(cell => cell.State),
                Builders<RuntimeCell>.Filter.Lte(cell => cell.UpdatedAtUtc, staleBefore)),
            Builders<RuntimeCell>.Filter.And(
                Builders<RuntimeCell>.Filter
                    .OfType<RuntimeCellState, DeletingRuntimeCellState>(cell => cell.State),
                Builders<RuntimeCell>.Filter.Or(
                    Builders<RuntimeCell>.Filter.And(
                        Builders<RuntimeCell>.Filter.Eq(cell => ((DeletingRuntimeCellState)cell.State).NextCheckAtUtc, null),
                        Builders<RuntimeCell>.Filter.Lte(cell => cell.UpdatedAtUtc, staleBefore)),
                    Builders<RuntimeCell>.Filter.And(
                        Builders<RuntimeCell>.Filter.Ne(cell => ((DeletingRuntimeCellState)cell.State).NextCheckAtUtc, null),
                        Builders<RuntimeCell>.Filter.Lte(cell => ((DeletingRuntimeCellState)cell.State).NextCheckAtUtc, now)))),
            Builders<RuntimeCell>.Filter.And(
                Builders<RuntimeCell>.Filter
                    .OfType<RuntimeCellState, FailedRuntimeCellState>(cell => cell.State),
                Builders<RuntimeCell>.Filter.Lte(
                    cell => ((FailedRuntimeCellState)cell.State).RetryNotBeforeUtc,
                    now)));
}
