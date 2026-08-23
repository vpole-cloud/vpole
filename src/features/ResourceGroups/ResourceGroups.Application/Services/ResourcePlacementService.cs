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
    LookupDeploymentTarget lookupDeploymentTarget,
    LookupDefaultDeploymentTarget lookupDefaultDeploymentTarget,
    AdmitDeploymentTargetForWorkload admitDeploymentTarget,
    AdmitDefaultDeploymentTargetForNewWorkload admitDefaultDeploymentTarget,
    ApplyDeploymentTargetReferenceChanges applyDeploymentTargetReferenceChanges,
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

        DeploymentTargetLookupDto requestedTarget;
        if (string.IsNullOrWhiteSpace(deploymentTargetId))
        {
            var targetResult = await lookupDefaultDeploymentTarget(cancellationToken);
            if (targetResult is Err<DefaultDeploymentTargetLookupError> error)
            {
                return LookupFailure(error.Value);
            }
            if (targetResult is not Ok<DeploymentTargetLookupDto> target)
                throw new InvalidOperationException(
                    "Default deployment-target lookup returned an unsupported result case.");

            requestedTarget = target.Value;
        }
        else
        {
            var targetResult = await lookupDeploymentTarget(
                deploymentTargetId.Trim(),
                cancellationToken);
            if (targetResult is Err<DeploymentTargetLookupError> error)
            {
                return LookupFailure(error.Value);
            }
            if (targetResult is not Ok<DeploymentTargetLookupDto> target)
                throw new InvalidOperationException(
                    "Deployment-target lookup returned an unsupported result case.");

            requestedTarget = target.Value;
        }

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

        DeploymentTargetLookupDto admittedTarget;
        if (string.IsNullOrWhiteSpace(deploymentTargetId))
        {
            var admissionResult = await admitDefaultDeploymentTarget(cancellationToken);
            if (admissionResult is Err<DefaultDeploymentTargetWorkloadAdmissionError> error)
                return AdmissionFailure(error.Value);
            if (admissionResult is not Ok<DeploymentTargetLookupDto> admission)
                throw new InvalidOperationException(
                    "Default deployment-target admission returned an unsupported result case.");

            admittedTarget = admission.Value;
        }
        else
        {
            var admissionResult = await admitDeploymentTarget(
                deploymentTargetId.Trim(),
                cancellationToken);
            if (admissionResult is Err<DeploymentTargetWorkloadAdmissionError> error)
                return AdmissionFailure(error.Value);
            if (admissionResult is not Ok<DeploymentTargetLookupDto> admission)
                throw new InvalidOperationException(
                    "Deployment-target admission returned an unsupported result case.");

            admittedTarget = admission.Value;
        }

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
            var referenceResult = await applyDeploymentTargetReferenceChanges(
                new DeploymentTargetReferenceOwner(ReferenceOwnerType, runtimeCell.Id),
                [runtimeCell.DeploymentTargetId],
                [],
                session,
                cancellationToken);
            if (referenceResult is Err<DeploymentTargetReferenceChangeRejected>)
            {
                return Failures.Conflict(
                    "The deployment target no longer accepts durable runtime-cell references.");
            }

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

    public async Task<Result<IReadOnlyList<ResourcePlacement>>> ResolveOrCreateManyAsync(
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
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["DeploymentTargetIds"] =
                    ["At least one unique deployment target ID is required."]
            });
        }

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

        var existing = await dbContext.RuntimeCells
            .Find(
                session,
                Builders<RuntimeCell>.Filter.And(
                    Builders<RuntimeCell>.Filter.Eq(cell => cell.TenantId, tenantId),
                    Builders<RuntimeCell>.Filter.Eq(
                        cell => cell.ResourceGroupId,
                        resourceGroup.Id),
                    Builders<RuntimeCell>.Filter.Eq(
                        cell => cell.IsolationScopeId,
                        scope.Id),
                    Builders<RuntimeCell>.Filter.In(
                        cell => cell.DeploymentTargetId,
                        normalizedTargetIds)))
            .ToListAsync(cancellationToken);
        var byTarget = existing.ToDictionary(
            cell => cell.DeploymentTargetId,
            StringComparer.Ordinal);

        var missingTargetIds = normalizedTargetIds
            .Where(targetId => !byTarget.ContainsKey(targetId))
            .ToArray();
        foreach (var targetId in missingTargetIds)
        {
            var admission = await admitDeploymentTarget(
                targetId,
                cancellationToken);
            if (admission is Err<DeploymentTargetWorkloadAdmissionError> error)
                return AdmissionFailure(error.Value);
            if (admission is not Ok<DeploymentTargetLookupDto> admittedTarget)
                throw new InvalidOperationException(
                    "Deployment-target admission returned an unsupported result case.");

            if (!string.Equals(admittedTarget.Value.Id, targetId, StringComparison.Ordinal))
            {
                return Failures.Conflict(
                    "Deployment-target admission resolved a different explicit target.");
            }
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var parentFence = await FenceParentsAsync(
            resourceGroup,
            scope,
            now,
            session,
            cancellationToken);
        if (parentFence is not ISuccess)
            return (Failure)parentFence;

        foreach (var targetId in missingTargetIds)
        {
            var candidate = RuntimeCellFactory.Create(
                tenantId,
                resourceGroup.Id,
                scope.Id,
                targetId,
                namespaceNameGenerator.Generate(tenantId, scope.Id),
                RuntimeCellFactory.DefaultOptions with
                {
                    CreatedAtUtc = now
                });
            var filter = Builders<RuntimeCell>.Filter.And(
                Builders<RuntimeCell>.Filter.Eq(cell => cell.TenantId, tenantId),
                Builders<RuntimeCell>.Filter.Eq(
                    cell => cell.ResourceGroupId,
                    resourceGroup.Id),
                Builders<RuntimeCell>.Filter.Eq(
                    cell => cell.IsolationScopeId,
                    scope.Id),
                Builders<RuntimeCell>.Filter.Eq(
                    cell => cell.DeploymentTargetId,
                    targetId));
            var runtimeCell = await dbContext.RuntimeCells.FindOneAndUpdateAsync(
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
                new FindOneAndUpdateOptions<RuntimeCell>
                {
                    IsUpsert = true,
                    ReturnDocument = ReturnDocument.After
                },
                cancellationToken);
            byTarget[targetId] = runtimeCell;

            if (!string.Equals(runtimeCell.Id, candidate.Id, StringComparison.Ordinal))
                continue;

            var referenceResult = await applyDeploymentTargetReferenceChanges(
                new DeploymentTargetReferenceOwner(ReferenceOwnerType, runtimeCell.Id),
                [runtimeCell.DeploymentTargetId],
                [],
                session,
                cancellationToken);
            if (referenceResult is Err<DeploymentTargetReferenceChangeRejected>)
            {
                return Failures.Conflict(
                    "The deployment target no longer accepts durable runtime-cell references.");
            }

            await dbContext.StoreEventAsync(
                OutboxWrite.Create(
                    runtimeCell.ToReconciliationRequestedEvent(),
                    RuntimeCellPendingKey.Reconciliation(
                        runtimeCell.Id,
                        runtimeCell.DesiredGeneration)),
                session,
                cancellationToken);
        }

        return normalizedTargetIds
            .Select(targetId => byTarget[targetId].ToPlacement())
            .ToArray();
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

    private static Failure LookupFailure(DefaultDeploymentTargetLookupError error) =>
        error switch
        {
            DefaultDeploymentTargetNotConfigured => Failures.Conflict(
                "The default deployment target is not configured."),
            _ => throw new InvalidOperationException(
                "Deployment-target lookup returned an unsupported error case.")
        };

    private static Failure LookupFailure(DeploymentTargetLookupError error) =>
        error switch
        {
            DeploymentTargetNotFound notFound => Failures.NotFound(
                "Deployment target not found.", notFound.DeploymentTargetId),
            _ => throw new InvalidOperationException(
                "Deployment-target lookup returned an unsupported error case.")
        };

    private static Failure AdmissionFailure(
        DefaultDeploymentTargetWorkloadAdmissionError error) =>
        error switch
        {
            DefaultDeploymentTargetNotConfigured => Failures.Conflict(
                "The default deployment target is not configured."),
            DeploymentTargetDisabled => Failures.Conflict(
                "The deployment target is disabled."),
            DeploymentTargetDeletionRequested => Failures.Conflict(
                "The deployment target is pending deletion."),
            DeploymentTargetNewWorkloadsDisabled => Failures.Conflict(
                "The deployment target does not accept new workloads."),
            _ => throw new InvalidOperationException(
                "Default deployment-target admission returned an unsupported error case.")
        };

    private static Failure AdmissionFailure(DeploymentTargetWorkloadAdmissionError error) =>
        error switch
        {
            DeploymentTargetNotFound notFound => Failures.NotFound(
                "Deployment target not found.", notFound.DeploymentTargetId),
            DeploymentTargetDisabled => Failures.Conflict(
                "The deployment target is disabled."),
            DeploymentTargetDeletionRequested => Failures.Conflict(
                "The deployment target is pending deletion."),
            DeploymentTargetNewWorkloadsDisabled => Failures.Conflict(
                "The deployment target does not accept new workloads."),
            _ => throw new InvalidOperationException(
                "Deployment-target admission returned an unsupported error case.")
        };

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
