using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Scheduling;
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
    public static BackupPolicyDto From(BackupPolicy p, DateTimeOffset now) => new(
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
        NextRun(p, now),
        p.Observation?.LastSuccessfulBackupAt,
        p.Observation?.WalArchivingStatus);

    private static DateTime? NextRun(BackupPolicy policy, DateTimeOffset now)
    {
        if (policy.Suspended || policy.Deleting || policy.AppliedRevision != policy.Revision)
            return null;
        if (policy.Observation?.NextScheduledAt is { } next && next > now.UtcDateTime)
            return next;
        return UtcCronSchedule.Parse(policy.Schedule).FindNextOccurrence(now)?.UtcDateTime;
    }

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
        o.NewResourceId,
        o.BackupId,
        o.TargetTime);
}
