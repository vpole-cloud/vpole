using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Kubernetes;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Services;

public sealed class ResourcePlacementService(
    IResourceGroupDbContext dbContext,
    IDeploymentTargetLookup deploymentTargetLookup,
    IDeploymentTargetReferenceAdmission referenceAdmission,
    IRuntimeCellNamespaceNameGenerator namespaceNameGenerator,
    TimeProvider timeProvider)
    : IResourcePlacementService
{
    public const string ReferenceOwnerType = "RuntimeCell";

    public async Task<Result<ResourcePlacement>> ResolveOrCreateAsync(
        string tenantId,
        string resourceGroupId,
        string? isolationScopeId,
        string? deploymentTargetId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);

        var resourceGroup = await dbContext.ResourceGroups
            .Find(session, group =>
                group.Id == resourceGroupId &&
                group.TenantId == tenantId)
            .FirstOrDefaultAsync(cancellationToken);
        if (resourceGroup is null)
            return Failures.NotFound("Resource group not found.", resourceGroupId);
        if (resourceGroup.Lifecycle is not ActiveResourceGroupLifecycleState)
            return Failures.Conflict("The resource group is being deleted.");

        var resolvedScopeId = string.IsNullOrWhiteSpace(isolationScopeId)
            ? resourceGroup.DefaultIsolationScopeId
            : isolationScopeId.Trim();
        var scope = await dbContext.IsolationScopes
            .Find(session, item =>
                item.Id == resolvedScopeId &&
                item.ResourceGroupId == resourceGroup.Id &&
                item.TenantId == tenantId)
            .FirstOrDefaultAsync(cancellationToken);
        if (scope is null)
            return Failures.NotFound("Isolation scope not found.", resolvedScopeId);
        if (scope.Lifecycle is not ActiveIsolationScopeLifecycleState)
            return Failures.Conflict("The isolation scope is being deleted.");

        var targetResult = string.IsNullOrWhiteSpace(deploymentTargetId)
            ? await deploymentTargetLookup.GetDefaultAsync(cancellationToken)
            : await deploymentTargetLookup.GetAsync(deploymentTargetId.Trim(), cancellationToken);
        if (!targetResult.TryGetValue(out var requestedTarget, out var targetFailure))
            return targetFailure;

        var existing = await FindAsync(
            scope.Id,
            requestedTarget.Id,
            session,
            cancellationToken);
        if (existing is not null)
        {
            var fenceResult = await FenceParentsAsync(
                resourceGroup,
                scope,
                timeProvider.GetUtcNow().UtcDateTime,
                session,
                cancellationToken);
            if (fenceResult is not ISuccess)
                return (Failure)fenceResult;
            return existing.ToPlacement();
        }

        var admissionResult = await deploymentTargetLookup.ResolveForNewWorkloadAsync(
            deploymentTargetId,
            cancellationToken);
        if (!admissionResult.TryGetValue(out var admittedTarget, out var admissionFailure))
            return admissionFailure;

        if (!string.Equals(admittedTarget.Id, requestedTarget.Id, StringComparison.Ordinal))
        {
            existing = await FindAsync(
                scope.Id,
                admittedTarget.Id,
                session,
                cancellationToken);
            if (existing is not null)
            {
                var fenceResult = await FenceParentsAsync(
                    resourceGroup,
                    scope,
                    timeProvider.GetUtcNow().UtcDateTime,
                    session,
                    cancellationToken);
                if (fenceResult is not ISuccess)
                    return (Failure)fenceResult;
                return existing.ToPlacement();
            }
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var candidate = RuntimeCellFactory.Create(
            tenantId,
            resourceGroup.Id,
            scope.Id,
            admittedTarget.Id,
            namespaceNameGenerator.Generate(tenantId, scope.Id),
            RuntimeCellFactory.DefaultOptions with
            {
                CreatedAtUtc = now
            });
        var parentFence = await FenceParentsAsync(
            resourceGroup,
            scope,
            now,
            session,
            cancellationToken);
        if (parentFence is not ISuccess)
            return (Failure)parentFence;

        var filter = Builders<RuntimeCell>.Filter.And(
            Builders<RuntimeCell>.Filter.Eq(cell => cell.IsolationScopeId, scope.Id),
            Builders<RuntimeCell>.Filter.Eq(cell => cell.DeploymentTargetId, admittedTarget.Id));
        var upsert = await dbContext.RuntimeCells.UpdateOneAsync(
            session,
            filter,
            Builders<RuntimeCell>.Update
                .SetOnInsert(cell => cell.Id, candidate.Id)
                .SetOnInsert(cell => cell.TenantId, candidate.TenantId)
                .SetOnInsert(cell => cell.ResourceGroupId, candidate.ResourceGroupId)
                .SetOnInsert(cell => cell.IsolationScopeId, candidate.IsolationScopeId)
                .SetOnInsert(cell => cell.DeploymentTargetId, candidate.DeploymentTargetId)
                .SetOnInsert(cell => cell.KubernetesNamespace, candidate.KubernetesNamespace)
                .SetOnInsert(cell => cell.DesiredGeneration, candidate.DesiredGeneration)
                .SetOnInsert(cell => cell.ObservedGeneration, candidate.ObservedGeneration)
                .SetOnInsert(cell => cell.State, candidate.State)
                .SetOnInsert(cell => cell.CreatedAtUtc, candidate.CreatedAtUtc)
                .SetOnInsert(cell => cell.UpdatedAtUtc, candidate.UpdatedAtUtc),
            new UpdateOptions { IsUpsert = true },
            cancellationToken);

        var runtimeCell = await dbContext.RuntimeCells
            .Find(session, filter)
            .SingleAsync(cancellationToken);
        if (upsert.UpsertedId is not null)
        {
            var referenceResult = await referenceAdmission.RegisterAsync(
                runtimeCell.DeploymentTargetId,
                new DeploymentTargetReferenceOwner(ReferenceOwnerType, runtimeCell.Id),
                session,
                cancellationToken);
            if (referenceResult is not ISuccess)
                return (Failure)referenceResult;

            await dbContext.StoreEventAsync(
                OutboxWrite.Create(
                    runtimeCell.ToReconciliationRequestedEvent(),
                    RuntimeCellPendingKey.Reconciliation(
                        runtimeCell.Id,
                        runtimeCell.DesiredGeneration)),
                session,
                cancellationToken);
        }

        return runtimeCell.ToPlacement();
    }

    private async Task<Result> FenceParentsAsync(
        ResourceGroup resourceGroup,
        IsolationScope scope,
        DateTime fencedAtUtc,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var resourceGroupFence = await dbContext.ResourceGroups.UpdateOneAsync(
            session,
            Builders<ResourceGroup>.Filter.And(
                Builders<ResourceGroup>.Filter.Eq(group => group.Id, resourceGroup.Id),
                Builders<ResourceGroup>.Filter.Eq(
                    group => group.TenantId,
                    resourceGroup.TenantId),
                Builders<ResourceGroup>.Filter
                    .OfType<ResourceGroupLifecycleState, ActiveResourceGroupLifecycleState>(
                        group => group.Lifecycle)),
            Builders<ResourceGroup>.Update
                .Inc(group => group.Revision, 1)
                .Set(group => group.UpdatedAtUtc, fencedAtUtc),
            cancellationToken: cancellationToken);
        if (resourceGroupFence.ModifiedCount != 1)
            return Failures.Conflict("The resource group changed while placement was being created.");

        var scopeFence = await dbContext.IsolationScopes.UpdateOneAsync(
            session,
            Builders<IsolationScope>.Filter.And(
                Builders<IsolationScope>.Filter.Eq(item => item.Id, scope.Id),
                Builders<IsolationScope>.Filter.Eq(
                    item => item.TenantId,
                    resourceGroup.TenantId),
                Builders<IsolationScope>.Filter
                    .OfType<IsolationScopeLifecycleState, ActiveIsolationScopeLifecycleState>(
                        item => item.Lifecycle)),
            Builders<IsolationScope>.Update
                .Inc(item => item.Revision, 1)
                .Set(item => item.UpdatedAtUtc, fencedAtUtc),
            cancellationToken: cancellationToken);
        if (scopeFence.ModifiedCount != 1)
            return Failures.Conflict("The isolation scope changed while placement was being created.");

        return Result.Success();
    }

    private async Task<RuntimeCell?> FindAsync(
        string isolationScopeId,
        string deploymentTargetId,
        IClientSessionHandle session,
        CancellationToken cancellationToken) =>
        await dbContext.RuntimeCells
            .Find(session, cell =>
                cell.IsolationScopeId == isolationScopeId &&
                cell.DeploymentTargetId == deploymentTargetId)
            .FirstOrDefaultAsync(cancellationToken);

    private static void EnsureTransaction(IClientSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsInTransaction)
        {
            throw new InvalidOperationException(
                "Resource placement must run inside the caller's MongoDB transaction.");
        }
    }
}
