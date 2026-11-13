using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Workloads;

namespace Pole.Features.Backups.Application.Configuration;

public sealed record BackupDestinationInput(
    string Name,
    string ResourceGroupId,
    string IsolationScopeId,
    string Endpoint,
    string Bucket,
    string Prefix,
    string CredentialSecretId,
    string AccessKeyIdKey,
    string SecretAccessKeyKey
)
{
    public BackupStorage Storage() => new(
        Endpoint,
        Bucket,
        Prefix.Trim('/'),
        CredentialSecretId,
        AccessKeyIdKey,
        SecretAccessKeyKey);
}

public sealed record BackupPolicyInput(
    string Name,
    string ResourceType,
    string ResourceId,
    string DestinationId,
    string Schedule,
    bool Suspended,
    int RetentionDays
);

public sealed record BackupConfigurationWrite<T>(
    string TenantId,
    string Id,
    long ExpectedRevision,
    T Input
);

public closed record BackupWriteResult<T>
{
    public sealed record Saved(
        T Value
    ) : BackupWriteResult<T>;

    public sealed record Rejected(
        string Code
    ) : BackupWriteResult<T>;
}

public sealed record BackupDestinationDto(
    string Id,
    string Name,
    string ResourceGroupId,
    string IsolationScopeId,
    string Endpoint,
    string Bucket,
    string Prefix,
    string CredentialSecretId,
    string AccessKeyIdKey,
    string SecretAccessKeyKey,
    long Revision,
    Pole.Features.GitOps.Contracts.GitResourceStatus? GitOps = null
)
{
    public static BackupDestinationDto From(BackupDestination d) => new(
        d.Id,
        d.Name,
        d.ResourceGroupId,
        d.IsolationScopeId,
        d.Storage.Endpoint,
        d.Storage.Bucket,
        d.Storage.Prefix,
        d.Storage.CredentialSecretId,
        d.Storage.AccessKeyIdKey,
        d.Storage.SecretAccessKeyKey,
        d.Revision);
}

public sealed record BackupPolicyDto(
    string Id,
    string Name,
    string ResourceType,
    string ResourceId,
    string DestinationId,
    string Schedule,
    bool Suspended,
    int RetentionDays,
    long Revision,
    long AppliedRevision,
    string ArchiveId,
    string? FailureCode,
    bool Deleting,
    DateTime? NextScheduledAt,
    DateTime? LastSuccessfulBackupAt,
    string? WalArchivingStatus,
    Pole.Features.GitOps.Contracts.GitResourceStatus? GitOps = null
);

public sealed record BackupArchiveDto(
    string Id,
    string ResourceType,
    string ResourceId,
    string ResourceName,
    string DestinationId,
    string ServerName,
    bool Active,
    DateTime CreatedAt,
    bool SupportsPointInTimeRecovery,
    int StorageGiB
);

public sealed record BackupOperationDto(
    string Id,
    string PolicyId,
    string ArchiveId,
    string Kind,
    string State,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? FailureCode,
    string? NewResourceId,
    string? BackupId,
    DateTime? TargetTime
);

public static class BackupViews
{
    public static BackupPolicyDto From(BackupPolicy p, BackupPolicyScheduleReport report) => new(
        p.Id,
        p.Name,
        p.ResourceType,
        p.ResourceId,
        p.DestinationId,
        p.Schedule,
        p.Suspended,
        p.RetentionDays,
        p.Revision,
        p.AppliedRevision,
        p.ArchiveId,
        p.FailureCode,
        p.Deleting,
        report.NextScheduledAt,
        report.LastSuccessfulBackupAt,
        report.WalArchivingStatus);

    public static BackupArchiveDto From(BackupArchive a, bool pitr) => new(
        a.Id,
        a.ResourceType,
        a.ResourceId,
        a.ResourceName,
        a.DestinationId,
        a.ServerName,
        a.Active,
        a.CreatedAt,
        pitr,
        a.StorageGiB);

    public static BackupOperationDto From(BackupOperation o) => new(
        o.Id,
        o.PolicyId,
        o.ArchiveId,
        o.Kind.ToString(),
        o.State.ToString(),
        o.CreatedAt,
        o.UpdatedAt,
        o.FailureCode,
        o.Work is RestoreBackupWork restore ? restore.Resource.Id : null,
        BackupId(o.Work),
        o.Work is RestoreBackupWork { Selection: BackupTimeSelection time } ? time.TargetTimeUtc : null);

    internal static string? BackupId(BackupOperationWork work) => work switch
    {
        ManualBackupWork { Execution: CompletedBackupCopy completed } => completed.BackupId,
        ScheduledBackupWork { Execution: CompletedScheduledBackup completed } => completed.BackupId,
        RestoreBackupWork { Selection: BackupCopySelection copy } => copy.BackupId,
        ManualBackupWork { Execution: RequestedBackupCopy or RunningBackupCopy or FailedBackupCopy } => null,
        ScheduledBackupWork
        {
            Execution: RunningScheduledBackup or FailedScheduledBackup or InterruptedScheduledBackup
        } => null,
        RestoreBackupWork { Selection: BackupTimeSelection } => null,
    };
}
