using Pole.Features.Backups.Domain;
using Pole.Features.Backups.Events;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.Backups.Application.Reconciliation;

internal static class BackupWorkRequests
{
    internal static OutboxWrite Policy(BackupPolicy policy) => OutboxWrite.Create(
        new BackupPolicyWorkRequestedEvent(
            policy.Id,
            policy.Dispatch.NextAttemptAtUtc,
            Core(policy.TenantId, policy.Dispatch.Origin)));

    internal static OutboxWrite Operation(BackupOperation operation) => OutboxWrite.Create(
        new BackupOperationWorkRequestedEvent(
            operation.Id,
            (operation.Dispatch ?? throw new InvalidOperationException("Only active commands can request dispatch."))
            .NextAttemptAtUtc,
            Core(operation.TenantId, operation.Origin)));

    internal static OutboxWrite OperationChanged(BackupOperation operation) => OutboxWrite.Create(
        new BackupOperationChangedEvent(
            operation.Id,
            operation.State.ToString(),
            Core(operation.TenantId, operation.Origin)));

    private static EventCore Core(string tenantId, BackupExecutionOrigin origin) => EventCore.Create(tenantId) with
    {
        TraceId = origin.TraceId, CorrelationId = origin.CorrelationId
    };
}
