using Microsoft.Extensions.Logging;
using Pole.ResultType;
using Pole.Observability;
using Pole.Features.ResourceGroups.Application.Kubernetes;
using Pole.Features.ResourceGroups.Application.ResourceGroups;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Events;

namespace Pole.Features.ResourceGroups.Application.RuntimeCells;

public sealed class RuntimeCellDeletionProgress(
    RuntimeCellLifecyclePersistence persistence,
    DeleteRuntimeCellNamespace deleteRuntimeCellNamespace,
    FinalizeResourceGroupDeletion finalizeResourceGroupDeletion,
    TimeProvider timeProvider,
    ILogger<RuntimeCellDeletionProgress>? logger = null)
{
    public async Task HandleAsync(
        RuntimeCellDeletionRequestedEvent message,
        CancellationToken cancellationToken = default)
    {
        using var trace = TraceContext.Push(TraceContext.Create(message.EventCore.TraceId, message.EventCore.CorrelationId));
        logger?.LogInformation("RuntimeCell deletion work started: TenantId={TenantId}, RuntimeCellId={RuntimeCellId}, Generation={Generation}, CheckSequence={CheckSequence}, NotBeforeUtc={NotBeforeUtc}, StartedAtUtc={StartedAtUtc}",
            message.EventCore.TenantId, message.RuntimeCellId, message.Generation, message.CheckSequence,
            message.NotBeforeUtc, timeProvider.GetUtcNow().UtcDateTime);
        var runtimeCell = await persistence.LoadAsync(
            message.EventCore.TenantId, message.RuntimeCellId, cancellationToken);
        if (runtimeCell is null || runtimeCell.DesiredGeneration != message.Generation ||
            runtimeCell.State is not DeletingRuntimeCellState deleting ||
            deleting.Generation != message.Generation || deleting.CheckSequence != message.CheckSequence ||
            deleting.NextCheckAtUtc != message.NotBeforeUtc ||
            deleting.NextCheckAtUtc > timeProvider.GetUtcNow().UtcDateTime)
            return;

        var namespaceResult = await deleteRuntimeCellNamespace(runtimeCell, cancellationToken);
        if (namespaceResult is Unit)
            logger?.LogInformation("RuntimeCell native absence confirmed: TenantId={TenantId}, RuntimeCellId={RuntimeCellId}, Generation={Generation}, CheckSequence={CheckSequence}, ObservedAtUtc={ObservedAtUtc}",
                runtimeCell.TenantId, runtimeCell.Id, message.Generation, message.CheckSequence,
                timeProvider.GetUtcNow().UtcDateTime);
        if (await persistence.PersistDeletionAsync(runtimeCell, message.Generation, namespaceResult, cancellationToken))
        {
            logger?.LogInformation("RuntimeCell deletion finalized and target reference released: TenantId={TenantId}, RuntimeCellId={RuntimeCellId}, Generation={Generation}, CheckSequence={CheckSequence}, CommittedAtUtc={CommittedAtUtc}",
                runtimeCell.TenantId, runtimeCell.Id, message.Generation, message.CheckSequence,
                timeProvider.GetUtcNow().UtcDateTime);
            await finalizeResourceGroupDeletion(runtimeCell.TenantId, runtimeCell.ResourceGroupId,
                runtimeCell.IsolationScopeId, cancellationToken);
        }
    }
}
