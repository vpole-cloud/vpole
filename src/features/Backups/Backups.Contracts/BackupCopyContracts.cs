namespace Pole.Features.Backups.Contracts;

public sealed record BackupCopyRequest(
    string OperationId,
    BackupArchiveContext Archive,
    BackupCredentialProjection Projection
);

public sealed record ObserveBackupCopyRequest(
    string OperationId,
    BackupArchiveContext Archive
);

public sealed record BackupCopyMissing;

public sealed record BackupCopyInProgress;

public sealed record BackupCopyCompleted(
    string BackupId
);

public sealed record BackupArchiveInactive;

public sealed record BackupPluginNotReady;

public sealed record BackupPluginUnavailable;

public sealed record BackupStorageAccessDenied;

public sealed record BackupStorageUnreachable;

public sealed record BackupDefinitionInvalid;

public sealed record BackupExecutionFailed;

public sealed record BackupStatusUnreadable;

public union ObserveBackupCopyOutcome(
    BackupCopyMissing,
    BackupCopyInProgress,
    BackupCopyCompleted,
    BackupSourceUnavailable,
    BackupNativeOwnershipConflict,
    BackupNativeIdentityUnavailable,
    BackupStatusUnreadable,
    BackupPluginUnavailable,
    BackupStorageAccessDenied,
    BackupStorageUnreachable,
    BackupDefinitionInvalid,
    BackupExecutionFailed);

public union RunBackupCopyOutcome(
    BackupCopyInProgress,
    BackupCopyCompleted,
    BackupSourceUnavailable,
    BackupNativeOwnershipConflict,
    BackupNativeIdentityUnavailable,
    BackupNativeWriteConflict,
    BackupArchiveInactive,
    BackupPluginNotReady,
    BackupStatusUnreadable,
    BackupPluginUnavailable,
    BackupStorageAccessDenied,
    BackupStorageUnreachable,
    BackupDefinitionInvalid,
    BackupExecutionFailed);
