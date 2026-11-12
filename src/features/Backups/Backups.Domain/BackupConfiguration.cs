using Pole.Multitenancy.Primitives;

namespace Pole.Features.Backups.Domain;

public interface IBackupDocument : ITenantOwned
{
    string Id { get; }
}

public interface IBackupConfigurationDocument : IBackupDocument
{
    string Name { get; }
    long Revision { get; }
}

public sealed record BackupStorage(
    string Endpoint,
    string Bucket,
    string Prefix,
    string CredentialSecretId,
    string AccessKeyIdKey,
    string SecretAccessKeyKey
);

public sealed record BackupDestination(
    string Id,
    string TenantId,
    string Name,
    string ResourceGroupId,
    string IsolationScopeId,
    BackupStorage Storage,
    long Revision,
    long ReferenceFence
) : IBackupConfigurationDocument;

public sealed record BackupPolicy(
    string Id,
    string TenantId,
    string Name,
    string ResourceType,
    string ResourceId,
    string DestinationId,
    string ArchiveId,
    string Schedule,
    bool Suspended,
    int RetentionDays,
    long Revision,
    long AppliedRevision,
    string? FailureCode,
    bool Deleting,
    DateTime NextAttemptAt,
    string? LeaseId,
    DateTime? LeaseUntil,
    long PlanningFence,
    BackupObservation? Observation
) : IBackupConfigurationDocument;

public sealed record ArchivePlacement(
    string ResourceGroupId,
    string IsolationScopeId,
    string DeploymentTargetId,
    string RuntimeCellId,
    string NamespaceName,
    string SourceDeploymentId,
    string CredentialDeploymentId,
    string StorageProfileId,
    long StorageProfileRevision,
    string ComputeProfileId,
    long ComputeProfileRevision
);

public sealed record BackupArchive(
    string Id,
    string TenantId,
    string PolicyId,
    string DestinationId,
    string ResourceType,
    string ResourceId,
    string ResourceName,
    long SourceRevision,
    string ServiceVersion,
    int StorageGiB,
    int Instances,
    BackupStorage Storage,
    ArchivePlacement Placement,
    string ServerName,
    bool Active,
    DateTime CreatedAt,
    long ReferenceFence
) : IBackupDocument;

public enum BackupOperationKind { Backup, Restore, ScheduledBackup }

public enum BackupOperationState { Pending, Running, Succeeded, Failed, Interrupted }

public sealed record BackupOperation(
    string Id,
    string TenantId,
    string IdempotencyKey,
    BackupOperationKind Kind,
    BackupOperationState State,
    string PolicyId,
    string ArchiveId,
    string? BackupId,
    [property:
        MongoDB.Bson.Serialization.Attributes.BsonDateTimeOptions(Representation = MongoDB.Bson.BsonType.Document)]
    DateTime? TargetTime,
    string? Name,
    string? NewResourceId,
    string? FailureCode,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime NextAttemptAt,
    string? LeaseId,
    DateTime? LeaseUntil,
    long PolicyRevision,
    int? RestoreStorageGiB,
    bool RestoreTargetCreated
) : IBackupDocument
{
    public static BackupOperation RequestCopy(
        string id,
        string tenant,
        string key,
        string policy,
        string archive,
        DateTime utcNow) => new(
        id,
        tenant,
        key,
        BackupOperationKind.Backup,
        BackupOperationState.Pending,
        policy,
        archive,
        null,
        null,
        null,
        null,
        null,
        utcNow,
        utcNow,
        utcNow,
        null,
        null,
        0,
        null,
        false);

    public static BackupOperation RequestRestore(
        string id,
        string tenant,
        string key,
        string policy,
        string archive,
        string? backupId,
        DateTime? targetTime,
        string name,
        string newResourceId,
        int? storageGiB,
        DateTime utcNow)
    {
        if ((backupId is null) == (targetTime is null) || backupId is not null && string.IsNullOrWhiteSpace(backupId))
            throw new ArgumentException("Restoration requires exactly one backup ID or timestamp.");
        if (targetTime is { Kind: not DateTimeKind.Utc })
            throw new ArgumentException("The recovery target must be UTC.", nameof(targetTime));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(newResourceId);
        if (storageGiB <= 0)
            throw new ArgumentOutOfRangeException(nameof(storageGiB));
        return new(
            id,
            tenant,
            key,
            BackupOperationKind.Restore,
            BackupOperationState.Pending,
            policy,
            archive,
            backupId,
            targetTime,
            name,
            newResourceId,
            null,
            utcNow,
            utcNow,
            utcNow,
            null,
            null,
            0,
            storageGiB,
            false);
    }

    public BackupOperation InterruptObservation(string reason, DateTime utcNow)
    {
        if (Kind != BackupOperationKind.ScheduledBackup || State != BackupOperationState.Running)
            throw new InvalidOperationException("Only a running scheduled backup can lose its final observation.");
        return this with { State = BackupOperationState.Interrupted, FailureCode = reason, UpdatedAt = utcNow };
    }

    public BackupOperation Advance(
        BackupOperationState state,
        string? failure,
        string? observedId,
        DateTime utcNow,
        TimeSpan retry)
    {
        if (State is BackupOperationState.Succeeded or BackupOperationState.Failed or BackupOperationState.Interrupted)
            throw new InvalidOperationException("A completed operation cannot be dispatched.");
        if (state == BackupOperationState.Failed && string.IsNullOrWhiteSpace(failure))
            throw new ArgumentException("A failed operation needs a failure code.", nameof(failure));
        return this with
        {
            State = state, FailureCode = failure,
            BackupId = Kind == BackupOperationKind.Backup ? BackupId ?? observedId : BackupId,
            UpdatedAt = utcNow, NextAttemptAt = utcNow + retry, LeaseId = null, LeaseUntil = null
        };
    }
}

public sealed record BackupObservation(
    string WalArchivingStatus,
    DateTime? WalStatusChangedAt,
    DateTime? LastSuccessfulBackupAt,
    DateTime? NextScheduledAt
);
