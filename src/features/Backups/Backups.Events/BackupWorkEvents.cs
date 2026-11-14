using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.Backups.Events;

[EntityName("pole.backups/v1/policy-work-requested")]
public sealed record BackupPolicyWorkRequestedEvent(
    string PolicyId,
    DateTime NotBeforeUtc,
    EventCore EventCore
) : IPoleEvent;

[EntityName("pole.backups/v1/operation-work-requested")]
public sealed record BackupOperationWorkRequestedEvent(
    string OperationId,
    DateTime NotBeforeUtc,
    EventCore EventCore
) : IPoleEvent;

[EntityName("pole.backups/v1/history-work-requested")]
public sealed record BackupHistoryWorkRequestedEvent(
    string ArchiveId,
    DateTime NotBeforeUtc,
    EventCore EventCore
) : IPoleEvent;
