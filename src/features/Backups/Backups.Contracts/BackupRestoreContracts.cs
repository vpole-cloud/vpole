namespace Pole.Features.Backups.Contracts;

public closed record BackupRecoveryTarget;

public sealed record BackupCopyRecoveryTarget(
    string BackupId
) : BackupRecoveryTarget;

public sealed record BackupTimeRecoveryTarget(
    DateTimeOffset TargetTime
) : BackupRecoveryTarget;

public sealed record RestoreBackupResource(
    string Id,
    string Name,
    int? StorageGiB
);

public sealed record RestoreBackupRequest(
    string OperationId,
    BackupArchiveContext Archive,
    BackupCredentialProjection Projection,
    RestoreBackupResource Resource,
    BackupRecoveryTarget Target
);
