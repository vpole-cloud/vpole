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
