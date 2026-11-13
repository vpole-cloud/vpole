namespace Pole.Features.Backups.Contracts;

public sealed record BackupSourceUnavailable;

public sealed record BackupSourceUnsupported;

public union DescribeBackupSourceOutcome(
    BackupServiceSnapshot,
    BackupSourceUnavailable,
    BackupSourceUnsupported);

public sealed record BackupScheduleAccepted;

public sealed record BackupRetentionInvalid;

public union ValidateBackupScheduleOutcome(BackupScheduleAccepted, BackupRetentionInvalid);
