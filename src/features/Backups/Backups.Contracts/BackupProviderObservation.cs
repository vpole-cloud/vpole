namespace Pole.Features.Backups.Contracts;

public sealed record BackupRuntimeObservation(
    string WalArchivingState,
    DateTimeOffset? WalStatusChangedAt,
    DateTimeOffset? LastSuccessfulBackupAt,
    DateTimeOffset? NextScheduledBackupAt
);

public union ObserveBackupRuntimeOutcome(
    BackupRuntimeObservation,
    BackupSourceUnavailable,
    BackupNativeOwnershipConflict,
    BackupNativeIdentityUnavailable);
