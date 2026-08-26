using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.Persistence.RuntimeCells;

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
                Builders<RuntimeCell>.Filter
                    .OfType<RuntimeCellState, ProvisioningRuntimeCellState>(cell => cell.State),
                Builders<RuntimeCell>.Filter.Lte(cell => cell.UpdatedAtUtc, staleBefore)),
            Builders<RuntimeCell>.Filter.And(
                Builders<RuntimeCell>.Filter
                    .OfType<RuntimeCellState, DeletingRuntimeCellState>(cell => cell.State),
                Builders<RuntimeCell>.Filter.Lte(cell => cell.UpdatedAtUtc, staleBefore)),
            Builders<RuntimeCell>.Filter.And(
                Builders<RuntimeCell>.Filter
                    .OfType<RuntimeCellState, FailedRuntimeCellState>(cell => cell.State),
                Builders<RuntimeCell>.Filter.Lte(
                    cell => ((FailedRuntimeCellState)cell.State).RetryNotBeforeUtc,
                    now)));
}
