using Pole.Features.Backups.Domain;

namespace Pole.Features.Backups.Application.Operations;

public sealed record BackupCommandIdempotencyConflict;

public union MatchBackupCommandOutcome(BackupOperation, BackupCommandIdempotencyConflict);

internal static class BackupOperationIdempotency
{
    internal static MatchBackupCommandOutcome Match(BackupOperation operation, ManualBackupCommand command) =>
        operation.Work is ManualBackupWork && operation.PolicyId == command.PolicyId
            ? operation
            : new BackupCommandIdempotencyConflict();

    internal static MatchBackupCommandOutcome Match(BackupOperation operation, RestoreArchiveCommand command) =>
        operation.Work is RestoreBackupWork restore && operation.ArchiveId == command.ArchiveId &&
        restore.Selection == command.Selection && restore.Resource.Name == command.Name &&
        restore.Resource.StorageGiB == command.StorageGiB
            ? operation
            : new BackupCommandIdempotencyConflict();
}
