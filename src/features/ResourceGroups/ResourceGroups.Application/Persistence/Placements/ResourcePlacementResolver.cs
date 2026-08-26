using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Kubernetes;
using Pole.Features.ResourceGroups.Application.Persistence.IsolationScopes;
using Pole.Features.ResourceGroups.Application.Persistence.RuntimeCells;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Persistence.Placements;

/// <summary>
/// Owns the caller-session placement protocol: lifecycle fences, runtime-cell
/// creation, target-reference registration, and the reconciliation outbox write.
/// </summary>
internal sealed class MongoResourcePlacementResolver(
    IMongoCollection<ResourceGroup> resourceGroups,
    IMongoCollection<IsolationScope> isolationScopes,
    IMongoCollection<RuntimeCell> runtimeCells,
    LookupDeploymentTarget lookupDeploymentTarget,
    LookupDefaultDeploymentTarget lookupDefaultDeploymentTarget,
    AdmitDeploymentTargetForWorkload admitDeploymentTarget,
    AdmitDefaultDeploymentTargetForNewWorkload admitDefaultDeploymentTarget,
    ApplyDeploymentTargetReferenceChanges applyDeploymentTargetReferenceChanges,
    GenerateRuntimeCellNamespaceName generateNamespaceName,
    StoreOutboxWrites storeOutboxWrites,
    TimeProvider timeProvider)
{
    public async Task<Result<ResourcePlacement, ResourceGroupPlacementError>> InvokeAsync(
        string tenantId,
        string resourceGroupId,
        string? isolationScopeId,
        string? deploymentTargetId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        var context = await LoadContextAsync(
            tenantId,
            resourceGroupId,
            isolationScopeId,
            session,
            cancellationToken);
        if (context.Error is not null)
            return new Err<ResourceGroupPlacementError>(context.Error.Value);

        var requested = await ResolveRequestedTargetAsync(
            deploymentTargetId,
            cancellationToken);
        if (requested.Error is not null)
            return new Err<ResourceGroupPlacementError>(requested.Error.Value);

        var existing = await runtimeCells
            .Find(
                session,
                RuntimeCellFilters.ByScopeAndDeploymentTarget(
                    context.Scope!.Id,
                    requested.Target!.Id))
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            var fenceError = await FenceParentsAsync(
                context.ResourceGroup!,
                context.Scope,
                timeProvider.GetUtcNow().UtcDateTime,
                session,
                cancellationToken);
            return fenceError is null
                ? new Ok<ResourcePlacement>(existing.ToPlacement())
                : new Err<ResourceGroupPlacementError>(fenceError.Value);
        }

        var admitted = await AdmitTargetAsync(deploymentTargetId, cancellationToken);
        if (admitted.Error is not null)
            return new Err<ResourceGroupPlacementError>(admitted.Error.Value);

        if (!string.Equals(admitted.Target!.Id, requested.Target!.Id, StringComparison.Ordinal))
        {
            existing = await runtimeCells
                .Find(
                    session,
                    RuntimeCellFilters.ByScopeAndDeploymentTarget(
                        context.Scope.Id,
                        admitted.Target.Id))
                .FirstOrDefaultAsync(cancellationToken);
            if (existing is not null)
            {
                var fenceError = await FenceParentsAsync(
                    context.ResourceGroup!,
                    context.Scope,
                    timeProvider.GetUtcNow().UtcDateTime,
                    session,
                    cancellationToken);
                return fenceError is null
                    ? new Ok<ResourcePlacement>(existing.ToPlacement())
                    : new Err<ResourceGroupPlacementError>(fenceError.Value);
            }
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var fence = await FenceParentsAsync(
            context.ResourceGroup!,
            context.Scope!,
            now,
            session,
            cancellationToken);
        if (fence is not null)
            return new Err<ResourceGroupPlacementError>(fence.Value);

        var runtimeCell = await UpsertRuntimeCellAsync(
            context.ResourceGroup!,
            context.Scope!,
            admitted.Target!.Id,
            now,
            session,
            cancellationToken);
        if (!runtimeCell.Created)
            return new Ok<ResourcePlacement>(runtimeCell.Cell.ToPlacement());

        var reference = await applyDeploymentTargetReferenceChanges(
            new DeploymentTargetReferenceOwner(
                ResourcePlacementReferenceOwnerTypes.RuntimeCell,
                runtimeCell.Cell.Id),
            [runtimeCell.Cell.DeploymentTargetId],
            [],
            session,
            cancellationToken);
        if (reference is Err<DeploymentTargetReferenceChangeRejected>)
        {
            return new Err<ResourceGroupPlacementError>(
                new ResourceGroupPlacementError(
                    new ResourceGroupPlacementDeploymentTargetReferenceRejected(
                        runtimeCell.Cell.DeploymentTargetId)));
        }

        await storeOutboxWrites(
            session,
            [OutboxWrite.Create(
                runtimeCell.Cell.ToReconciliationRequestedEvent(),
                RuntimeCellPendingKey.Reconciliation(
                    runtimeCell.Cell.Id,
                    runtimeCell.Cell.DesiredGeneration))],
            cancellationToken);
        return new Ok<ResourcePlacement>(runtimeCell.Cell.ToPlacement());
    }

    public async Task<Result<IReadOnlyList<ResourcePlacement>, ResourceGroupPlacementError>>
        InvokeManyAsync(
            string tenantId,
            string resourceGroupId,
            string? isolationScopeId,
            IReadOnlyList<string> deploymentTargetIds,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        ArgumentNullException.ThrowIfNull(deploymentTargetIds);
        var normalizedTargetIds = deploymentTargetIds
            .Select(targetId => string.IsNullOrWhiteSpace(targetId)
                ? string.Empty
                : targetId.Trim())
            .ToArray();
        if (normalizedTargetIds.Length == 0 ||
            normalizedTargetIds.Any(string.IsNullOrEmpty) ||
            normalizedTargetIds.Distinct(StringComparer.Ordinal).Count() !=
            normalizedTargetIds.Length)
        {
            throw new ArgumentException(
                "At least one unique deployment target ID is required.",
                nameof(deploymentTargetIds));
        }

        var context = await LoadContextAsync(
            tenantId,
            resourceGroupId,
            isolationScopeId,
            session,
            cancellationToken);
        if (context.Error is not null)
            return new Err<ResourceGroupPlacementError>(context.Error.Value);

        var existing = await runtimeCells
            .Find(
                session,
                RuntimeCellFilters.ByTenantScopeAndDeploymentTargets(
                    tenantId,
                    context.ResourceGroup!.Id,
                    context.Scope!.Id,
                    normalizedTargetIds))
            .ToListAsync(cancellationToken);
        var byTarget = existing.ToDictionary(
            cell => cell.DeploymentTargetId,
            StringComparer.Ordinal);
        var missingTargetIds = normalizedTargetIds
            .Where(targetId => !byTarget.ContainsKey(targetId))
            .ToArray();

        foreach (var targetId in missingTargetIds)
        {
            var admitted = await AdmitTargetAsync(targetId, cancellationToken);
            if (admitted.Error is not null)
                return new Err<ResourceGroupPlacementError>(admitted.Error.Value);
            if (!string.Equals(admitted.Target!.Id, targetId, StringComparison.Ordinal))
            {
                return new Err<ResourceGroupPlacementError>(
                    new ResourceGroupPlacementError(
                        new ResourceGroupPlacementTargetResolutionChanged(
                            targetId,
                            admitted.Target.Id)));
            }
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var fence = await FenceParentsAsync(
            context.ResourceGroup!,
            context.Scope!,
            now,
            session,
            cancellationToken);
        if (fence is not null)
            return new Err<ResourceGroupPlacementError>(fence.Value);

        foreach (var targetId in missingTargetIds)
        {
            var runtimeCell = await UpsertRuntimeCellAsync(
                context.ResourceGroup!,
                context.Scope!,
                targetId,
                now,
                session,
                cancellationToken);
            byTarget[targetId] = runtimeCell.Cell;
            if (!runtimeCell.Created)
                continue;

            var reference = await applyDeploymentTargetReferenceChanges(
                new DeploymentTargetReferenceOwner(
                    ResourcePlacementReferenceOwnerTypes.RuntimeCell,
                    runtimeCell.Cell.Id),
                [runtimeCell.Cell.DeploymentTargetId],
                [],
                session,
                cancellationToken);
            if (reference is Err<DeploymentTargetReferenceChangeRejected>)
            {
                return new Err<ResourceGroupPlacementError>(
                    new ResourceGroupPlacementError(
                        new ResourceGroupPlacementDeploymentTargetReferenceRejected(
                            runtimeCell.Cell.DeploymentTargetId)));
            }

            await storeOutboxWrites(
                session,
                [OutboxWrite.Create(
                    runtimeCell.Cell.ToReconciliationRequestedEvent(),
                    RuntimeCellPendingKey.Reconciliation(
                        runtimeCell.Cell.Id,
                        runtimeCell.Cell.DesiredGeneration))],
                cancellationToken);
        }

        return new Ok<IReadOnlyList<ResourcePlacement>>(
            normalizedTargetIds
                .Select(targetId => byTarget[targetId].ToPlacement())
                .ToArray());
    }

    private async Task<PlacementContext> LoadContextAsync(
        string tenantId,
        string resourceGroupId,
        string? isolationScopeId,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var resourceGroup = await resourceGroups
            .Find(session, ResourceGroupFilters.ByTenantAndId(tenantId, resourceGroupId))
            .FirstOrDefaultAsync(cancellationToken);
        if (resourceGroup is null)
        {
            return new PlacementContext(
                null,
                null,
                new ResourceGroupPlacementError(
                    new ResourceGroupPlacementResourceGroupNotFound(resourceGroupId)));
        }
        if (resourceGroup.Lifecycle is DeletingResourceGroupLifecycleState)
        {
            return new PlacementContext(
                null,
                null,
                new ResourceGroupPlacementError(
                    new ResourceGroupPlacementResourceGroupDeleting(resourceGroupId)));
        }
        if (resourceGroup.Lifecycle is not ActiveResourceGroupLifecycleState)
        {
            throw new InvalidOperationException(
                $"Resource group '{resourceGroup.Id}' has an unsupported lifecycle state.");
        }

        var resolvedScopeId = string.IsNullOrWhiteSpace(isolationScopeId)
            ? resourceGroup.DefaultIsolationScopeId
            : isolationScopeId.Trim();
        var scope = await isolationScopes
            .Find(
                session,
                IsolationScopeFilters.ByTenantResourceGroupAndId(
                    tenantId,
                    resourceGroup.Id,
                    resolvedScopeId))
            .FirstOrDefaultAsync(cancellationToken);
        if (scope is null)
        {
            return new PlacementContext(
                null,
                null,
                new ResourceGroupPlacementError(
                    new ResourceGroupPlacementIsolationScopeNotFound(resolvedScopeId)));
        }
        if (scope.Lifecycle is DeletingIsolationScopeLifecycleState)
        {
            return new PlacementContext(
                null,
                null,
                new ResourceGroupPlacementError(
                    new ResourceGroupPlacementIsolationScopeDeleting(scope.Id)));
        }
        if (scope.Lifecycle is not ActiveIsolationScopeLifecycleState)
        {
            throw new InvalidOperationException(
                $"Isolation scope '{scope.Id}' has an unsupported lifecycle state.");
        }

        return new PlacementContext(resourceGroup, scope, null);
    }

    private async Task<TargetResolution> ResolveRequestedTargetAsync(
        string? deploymentTargetId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deploymentTargetId))
        {
            var defaultTarget = await lookupDefaultDeploymentTarget(cancellationToken);
            return defaultTarget switch
            {
                Ok<DeploymentTargetLookupDto> success => new TargetResolution(success.Value, null),
                Err<DefaultDeploymentTargetLookupError> => RejectedTarget(null)
            };
        }

        var requestedTargetId = deploymentTargetId.Trim();
        var target = await lookupDeploymentTarget(requestedTargetId, cancellationToken);
        return target switch
        {
            Ok<DeploymentTargetLookupDto> success => new TargetResolution(success.Value, null),
            Err<DeploymentTargetLookupError> => RejectedTarget(requestedTargetId)
        };
    }

    private async Task<TargetResolution> AdmitTargetAsync(
        string? deploymentTargetId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deploymentTargetId))
        {
            var defaultTarget = await admitDefaultDeploymentTarget(cancellationToken);
            return defaultTarget switch
            {
                Ok<DeploymentTargetLookupDto> success => new TargetResolution(success.Value, null),
                Err<DefaultDeploymentTargetWorkloadAdmissionError> => RejectedTarget(null)
            };
        }

        var requestedTargetId = deploymentTargetId.Trim();
        var target = await admitDeploymentTarget(requestedTargetId, cancellationToken);
        return target switch
        {
            Ok<DeploymentTargetLookupDto> success => new TargetResolution(success.Value, null),
            Err<DeploymentTargetWorkloadAdmissionError> => RejectedTarget(requestedTargetId)
        };
    }

    private async Task<UpsertedRuntimeCell> UpsertRuntimeCellAsync(
        ResourceGroup resourceGroup,
        IsolationScope scope,
        string deploymentTargetId,
        DateTime now,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var candidate = RuntimeCellFactory.Create(
            resourceGroup.TenantId,
            resourceGroup.Id,
            scope.Id,
            deploymentTargetId,
            generateNamespaceName(resourceGroup.TenantId, scope.Id),
            RuntimeCellFactory.DefaultOptions with { CreatedAtUtc = now });
        var runtimeCell = await runtimeCells.FindOneAndUpdateAsync(
            session,
            RuntimeCellFilters.ByScopeAndDeploymentTarget(scope.Id, deploymentTargetId),
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
            new FindOneAndUpdateOptions<RuntimeCell>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After
            },
            cancellationToken);
        return new UpsertedRuntimeCell(
            runtimeCell,
            string.Equals(runtimeCell.Id, candidate.Id, StringComparison.Ordinal));
    }

    private async Task<ResourceGroupPlacementError?> FenceParentsAsync(
        ResourceGroup resourceGroup,
        IsolationScope scope,
        DateTime fencedAtUtc,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var resourceGroupFence = await resourceGroups.UpdateOneAsync(
            session,
            ResourceGroupFilters.ActiveAtRevision(
                resourceGroup.TenantId,
                resourceGroup.Id,
                resourceGroup.Revision),
            Builders<ResourceGroup>.Update
                .Inc(group => group.Revision, 1)
                .Set(group => group.UpdatedAtUtc, fencedAtUtc),
            cancellationToken: cancellationToken);
        if (resourceGroupFence.ModifiedCount != 1)
        {
            return new ResourceGroupPlacementError(
                new ResourceGroupPlacementChanged(resourceGroup.Id));
        }

        var scopeFence = await isolationScopes.UpdateOneAsync(
            session,
            IsolationScopeFilters.ActiveAtRevision(
                resourceGroup.TenantId,
                resourceGroup.Id,
                scope.Id,
                scope.Revision),
            Builders<IsolationScope>.Update
                .Inc(item => item.Revision, 1)
                .Set(item => item.UpdatedAtUtc, fencedAtUtc),
            cancellationToken: cancellationToken);
        if (scopeFence.ModifiedCount == 1)
            return null;

        return new ResourceGroupPlacementError(
            new ResourceGroupPlacementChanged(resourceGroup.Id));
    }

    private static TargetResolution RejectedTarget(string? deploymentTargetId) =>
        new(
            null,
            new ResourceGroupPlacementError(
                new ResourceGroupPlacementDeploymentTargetRejected(deploymentTargetId)));

    private static void EnsureTransaction(IClientSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsInTransaction)
        {
            throw new InvalidOperationException(
                "Resource placement must run inside the caller's MongoDB transaction.");
        }
    }

    private sealed record PlacementContext(
        ResourceGroup? ResourceGroup,
        IsolationScope? Scope,
        ResourceGroupPlacementError? Error);

    private sealed record TargetResolution(
        DeploymentTargetLookupDto? Target,
        ResourceGroupPlacementError? Error);

    private sealed record UpsertedRuntimeCell(RuntimeCell Cell, bool Created);
}
