// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Backups.Contracts;

public sealed record BackupSchedule(
    string CronUtc,
    bool Suspended,
    int RetentionDays
);

public closed record BackupPolicyConfiguration;

public sealed record ConfigureBackupPolicy(
    BackupSchedule Schedule,
    BackupCredentialProjection Projection
) : BackupPolicyConfiguration;

public sealed record StopBackupPolicy : BackupPolicyConfiguration;

public sealed record ApplyBackupScheduleRequest(
    string PolicyId,
    BackupArchiveContext Archive,
    BackupPolicyConfiguration Configuration,
    long Revision,
    BackupArchiveDrainAuthorization Drain
);

public closed record BackupArchiveDrainAuthorization;

public sealed record UnconfirmedBackupArchiveDrain : BackupArchiveDrainAuthorization;

public sealed record ConfirmedBackupArchiveDrain(
    string ArchiveId,
    NativeBackupScheduleSuspension Suspension
) : BackupArchiveDrainAuthorization;

public sealed record BackupPolicyDrainRequired(
    string ArchiveId,
    NativeBackupScheduleSuspension Suspension
);

public sealed record BackupPolicyApplied;

public sealed record BackupPolicyPending;

public sealed record BackupNativeOwnershipConflict;

public sealed record BackupNativeIdentityUnavailable;

public sealed record BackupNativeWriteConflict;

public sealed record BackupPolicyAlreadyActive;

public sealed record BackupPolicyRevisionUnreadable;

public sealed record BackupPolicySuperseded;

public sealed record BackupPluginConflict;

public union ApplyBackupScheduleOutcome(
    BackupPolicyApplied,
    BackupPolicyPending,
    BackupPolicyDrainRequired,
    BackupSourceUnavailable,
    BackupNativeOwnershipConflict,
    BackupNativeIdentityUnavailable,
    BackupNativeWriteConflict,
    BackupPolicyAlreadyActive,
    BackupPolicyRevisionUnreadable,
    BackupPolicySuperseded,
    BackupPluginConflict);
