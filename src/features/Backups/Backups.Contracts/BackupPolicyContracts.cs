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
    long Revision
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
    BackupSourceUnavailable,
    BackupNativeOwnershipConflict,
    BackupNativeIdentityUnavailable,
    BackupNativeWriteConflict,
    BackupPolicyAlreadyActive,
    BackupPolicyRevisionUnreadable,
    BackupPolicySuperseded,
    BackupPluginConflict);
