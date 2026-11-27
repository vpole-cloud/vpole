// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

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

public sealed record ObserveBackupRestoreRequest(
    string OperationId,
    BackupArchiveContext Archive,
    string ResourceId,
    BackupRecoveryTarget Target
);

public sealed record BackupRestoreTargetMissing;

public sealed record BackupRestoreInProgress;

public sealed record BackupRestoreCompleted(
    string ResourceId
);

public sealed record BackupRestoreIdentityConflict;

public sealed record BackupRestorePlacementChanged;

public sealed record BackupRestoreTargetMismatch;

public sealed record BackupRestoreRecoveryFailed;

public sealed record BackupRestoreTimePrecisionUnsupported;

public sealed record BackupRestoreStorageTooSmall;

public sealed record BackupRestoreVersionUnsupported;

public sealed record BackupRestoreNameAlreadyExists;

public sealed record BackupRestoreResourceDeleted;

public sealed record BackupRestoreQuotaRejected;

public sealed record BackupRestoreTargetRejected;

public sealed record BackupRestoreComputeRejected;

public sealed record BackupRestoreStorageRejected;

public sealed record BackupRestoreCredentialRejected;

public sealed record BackupRestoreOperationUnavailable;

public sealed record BackupRestorePlacementRejected;

public union ObserveBackupRestoreOutcome(
    BackupRestoreTargetMissing,
    BackupRestoreInProgress,
    BackupRestoreCompleted,
    BackupRestoreIdentityConflict,
    BackupRestorePlacementChanged,
    BackupRestoreTargetMismatch,
    BackupRestoreRecoveryFailed,
    BackupNativeIdentityUnavailable);

public union RestoreBackupOutcome(
    BackupRestoreInProgress,
    BackupRestoreCompleted,
    BackupRestoreIdentityConflict,
    BackupRestorePlacementChanged,
    BackupRestoreTargetMismatch,
    BackupRestoreRecoveryFailed,
    BackupNativeIdentityUnavailable,
    BackupNativeOwnershipConflict,
    BackupNativeWriteConflict,
    BackupRestoreTimePrecisionUnsupported,
    BackupRestoreStorageTooSmall,
    BackupRestoreVersionUnsupported,
    BackupRestoreNameAlreadyExists,
    BackupRestoreResourceDeleted,
    BackupRestoreQuotaRejected,
    BackupRestoreTargetRejected,
    BackupRestoreComputeRejected,
    BackupRestoreStorageRejected,
    BackupRestoreCredentialRejected,
    BackupRestoreOperationUnavailable,
    BackupRestorePlacementRejected);
