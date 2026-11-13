namespace Pole.Features.Backups.Contracts;

public sealed record BackupSourceIdentity(
    string TenantId,
    string ResourceType,
    string ResourceId
);

public sealed record BackupServiceSnapshot(
    string Version,
    int StorageGiB,
    int Instances,
    long Revision
);

public sealed record BackupSourcePlacement(
    string ResourceGroupId,
    string IsolationScopeId,
    string DeploymentTargetId,
    string RuntimeCellId,
    string NamespaceName,
    string DeploymentId,
    string StorageProfileId,
    long StorageProfileRevision,
    string ComputeProfileId,
    long ComputeProfileRevision
);

public sealed record BackupArchiveContext(
    string ArchiveId,
    BackupSourceIdentity Source,
    BackupServiceSnapshot Service,
    BackupSourcePlacement Placement,
    string Endpoint,
    string Bucket,
    string Prefix,
    string ServerName,
    string NativeCredentialName,
    string AccessKeyIdKey,
    string SecretAccessKeyKey
);

public sealed record BackupSchedule(
    string CronUtc,
    bool Suspended,
    int RetentionDays
);

public sealed record ApplyBackupScheduleRequest(
    string PolicyId,
    BackupArchiveContext Archive,
    BackupSchedule? Schedule,
    long Revision
);

public sealed record BackupCatalogEntry(
    string BackupId,
    DateTimeOffset CompletedAt
);

public sealed record BackupCatalogCredentials(
    string AccessKeyId,
    string SecretAccessKey
)
{
    public override string ToString() => "BackupCatalogCredentials([REDACTED])";
}

public sealed record ListBackupCatalogRequest(
    BackupArchiveContext Archive,
    BackupCatalogCredentials Credentials
);

public sealed record BackupCopyRequest(
    string OperationId,
    BackupArchiveContext Archive
);

public sealed record RestoreBackupRequest(
    string OperationId,
    BackupArchiveContext Archive,
    string NewResourceId,
    string Name,
    string? BackupId,
    DateTimeOffset? TargetTime,
    int? StorageGiB = null
);

public abstract record BackupProviderResult<T>
{
    public sealed record Success(
        T Value
    ) : BackupProviderResult<T>;

    public sealed record Failure(
        string Code,
        bool Retryable
    ) : BackupProviderResult<T>;
}

public enum BackupProviderPhase { Running, Succeeded }

public enum BackupObservedRunState { Running, Succeeded, Failed }

public sealed record BackupObservedRun(
    string NativeUid,
    string? BackupId,
    BackupObservedRunState State,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? FailureCode
);

public sealed record BackupRuntimeObservation(
    string WalArchivingState,
    DateTimeOffset? WalStatusChangedAt,
    DateTimeOffset? LastSuccessfulBackupAt,
    DateTimeOffset? NextScheduledBackupAt,
    IReadOnlyList<BackupObservedRun> Runs
);

public sealed record BackupProviderProgress(
    BackupProviderPhase Phase,
    string? ProviderBackupId
);

/// <summary>Providers may be called repeatedly after an interrupted dispatch. The operation ID identifies a single durable effect.</summary>
public interface IBackupProvider
{
    string ResourceType { get; }
    string DeclarationKind { get; }
    bool SupportsPointInTimeRecovery { get; }
    ValidateBackupScheduleOutcome ValidateSchedule(BackupSchedule schedule);
    Task<DescribeBackupSourceOutcome> DescribeAsync(BackupSourceIdentity source, CancellationToken ct);

    Task<BackupProviderResult<BackupProviderProgress>> ApplyScheduleAsync(
        ApplyBackupScheduleRequest request,
        CancellationToken ct);

    Task<BackupProviderResult<IReadOnlyList<BackupCatalogEntry>>> ListAsync(
        ListBackupCatalogRequest request,
        CancellationToken ct);

    Task<BackupProviderResult<BackupRuntimeObservation>> ObserveAsync(
        BackupArchiveContext archive,
        CancellationToken ct);

    Task<BackupProviderResult<BackupProviderProgress>> RunAsync(BackupCopyRequest request, CancellationToken ct);
    Task<BackupProviderResult<BackupProviderProgress>> RestoreAsync(RestoreBackupRequest request, CancellationToken ct);
}
