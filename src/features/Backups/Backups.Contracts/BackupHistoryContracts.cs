namespace Pole.Features.Backups.Contracts;

public sealed record NativeBackupStream(
    string PublisherId,
    string Version
);

public sealed record NativeBackupScheduleSuspension(
    string PolicyId,
    long PolicyRevision,
    string PublisherId
);

public closed record NativeBackupHistoryPosition;

public sealed record BeginNativeBackupHistory : NativeBackupHistoryPosition;

public sealed record PageNativeBackupHistory(
    NativeBackupStream Stream,
    string Continuation
) : NativeBackupHistoryPosition;

public sealed record FollowNativeBackupHistory(
    NativeBackupStream Stream
) : NativeBackupHistoryPosition;

public closed record NativeBackupHistoryMode;

public sealed record LiveNativeBackupHistory : NativeBackupHistoryMode;

public sealed record SuspendedNativeBackupHistory(
    NativeBackupScheduleSuspension Suspension
) : NativeBackupHistoryMode;

public sealed record ReadBackupHistoryRequest(
    string PolicyId,
    BackupArchiveContext Archive,
    NativeBackupHistoryMode Mode,
    NativeBackupHistoryPosition Position
);

public closed record NativeScheduledBackupState;

public sealed record NativeScheduledBackupRunning : NativeScheduledBackupState;

public sealed record NativeScheduledBackupCompleted(
    string BackupId
) : NativeScheduledBackupState;

public sealed record NativeScheduledBackupFailed(
    string Reason
) : NativeScheduledBackupState;

public sealed record NativeScheduledBackupRemoved : NativeScheduledBackupState;

public sealed record NativeScheduledBackupChange(
    string NativeUid,
    NativeScheduledBackupState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public sealed record NativeBackupActivity(
    string NativeUid,
    bool InUse
);

/// <summary>A bounded Kubernetes metadata step, never an object-store backup catalog.</summary>
public sealed record BackupHistoryBatch(
    NativeBackupHistoryPosition Position,
    long ObservedPolicyRevision,
    IReadOnlyList<NativeBackupActivity> Activities,
    IReadOnlyList<NativeScheduledBackupChange> Changes,
    int ConsumedCount
);

public sealed record BackupHistoryPending;

public sealed record BackupHistoryRestartRequired;

public sealed record BackupHistoryUnreadable;

public sealed record BackupHistoryDeadlineReached;

public sealed record BackupHistorySuspensionChanged;

public union ReadBackupHistoryOutcome(
    BackupHistoryBatch,
    BackupHistoryPending,
    BackupHistoryRestartRequired,
    BackupHistoryUnreadable,
    BackupHistoryDeadlineReached,
    BackupHistorySuspensionChanged,
    BackupSourceUnavailable,
    BackupNativeOwnershipConflict,
    BackupNativeIdentityUnavailable);

public sealed record SuspendBackupArchiveRequest(
    string PolicyId,
    BackupArchiveContext Archive,
    long PolicyRevision
);

public sealed record BackupArchiveSuspended(
    NativeBackupScheduleSuspension Suspension
);

public union SuspendBackupArchiveOutcome(
    BackupArchiveSuspended,
    BackupSourceUnavailable,
    BackupNativeOwnershipConflict,
    BackupNativeIdentityUnavailable,
    BackupNativeWriteConflict,
    BackupPolicyRevisionUnreadable,
    BackupPolicySuperseded);
