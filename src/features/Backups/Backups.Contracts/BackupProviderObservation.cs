namespace Pole.Features.Backups.Contracts;

public closed record BackupProviderResult<T>
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
