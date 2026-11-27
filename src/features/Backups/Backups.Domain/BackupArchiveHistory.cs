// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.Backups.Domain;

public sealed record BackupHistoryStream(
    string PublisherId,
    string Version
);

public sealed record BackupScheduleSuspension(
    string PolicyId,
    long PolicyRevision,
    string PublisherId
);

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(InitialBackupHistoryPosition),
    typeof(PagedBackupHistoryPosition),
    typeof(FollowingBackupHistoryPosition),
    typeof(FinishingBackupHistoryPosition))]
public closed record BackupHistoryPosition;

public sealed record InitialBackupHistoryPosition : BackupHistoryPosition;

public sealed record PagedBackupHistoryPosition(
    BackupHistoryStream Stream,
    string Continuation
) : BackupHistoryPosition;

public sealed record FinishingBackupHistoryPosition(
    BackupHistoryStream Stream
) : BackupHistoryPosition;

public sealed record FollowingBackupHistoryPosition(
    BackupHistoryStream Stream
) : BackupHistoryPosition;

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(LiveBackupHistory), typeof(PreparingBackupHistoryDrain), typeof(DrainingBackupHistory))]
public closed record BackupHistoryMode;

public sealed record LiveBackupHistory : BackupHistoryMode;

public sealed record PreparingBackupHistoryDrain(
    long PolicyRevision
) : BackupHistoryMode;

public sealed record DrainingBackupHistory(
    BackupScheduleSuspension Suspension
) : BackupHistoryMode;

public sealed record BackupHistoryCheckpoint(
    string ScanId,
    BackupHistoryPosition Position,
    ImmutableHashSet<string> ActiveNativeIds
);

public sealed record BackupHistoryStart(
    string ScanId,
    BackupExecutionOrigin Origin
);

public sealed record BackupNativeActivity(
    string NativeUid,
    bool InUse
);

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(MonitoringBackupHistory), typeof(QuiescentBackupHistory), typeof(StoppedBackupHistory))]
public closed record BackupArchiveHistory(
    long Revision
)
{
    [BsonIgnore]
    public BackupExecutionOrigin ExecutionOrigin =>
        this switch
        {
            MonitoringBackupHistory monitoring => monitoring.Dispatch.Origin,
            QuiescentBackupHistory quiescent => quiescent.Origin,
            StoppedBackupHistory stopped => stopped.Origin,
        };

    public static BackupArchiveHistory Begin(BackupHistoryStart start, DateTime utcNow) => new MonitoringBackupHistory(
        1,
        new LiveBackupHistory(),
        InitialCheckpoint(start.ScanId),
        new AvailableBackupDispatch(utcNow, start.Origin),
        null);

    public BackupArchiveHistory PrepareDrain(
        long policyRevision,
        BackupHistoryStart start,
        DateTime utcNow) => this switch
    {
        MonitoringBackupHistory { Mode: LiveBackupHistory } => new MonitoringBackupHistory(
            Revision + 1,
            new PreparingBackupHistoryDrain(policyRevision),
            InitialCheckpoint(start.ScanId),
            new AvailableBackupDispatch(utcNow, start.Origin),
            null),
        MonitoringBackupHistory or QuiescentBackupHistory or StoppedBackupHistory => this,
    };

    public BackupArchiveHistory RecheckDrain(DateTime utcNow) => this switch
    {
        MonitoringBackupHistory { Mode: DrainingBackupHistory drain } monitoring => monitoring with
        {
            Revision = Revision + 1,
            Mode = new PreparingBackupHistoryDrain(drain.Suspension.PolicyRevision),
            Dispatch = new AvailableBackupDispatch(utcNow, ExecutionOrigin),
        },
        MonitoringBackupHistory or QuiescentBackupHistory or StoppedBackupHistory =>
            throw new InvalidOperationException("Only a native drain can recheck its suspension."),
    };

    public BackupArchiveHistory RequestDrain(
        BackupScheduleSuspension suspension,
        BackupHistoryStart start,
        DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suspension.PolicyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(suspension.PublisherId);
        if (suspension.PolicyRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(suspension));
        return this switch
        {
            MonitoringBackupHistory { Mode: DrainingBackupHistory existing } when existing.Suspension == suspension =>
                this,
            QuiescentBackupHistory existing when existing.Suspension == suspension => this,
            MonitoringBackupHistory or QuiescentBackupHistory => new MonitoringBackupHistory(
                Revision + 1,
                new DrainingBackupHistory(suspension),
                InitialCheckpoint(start.ScanId),
                new AvailableBackupDispatch(utcNow, start.Origin),
                null),
            StoppedBackupHistory => throw new InvalidOperationException(
                "A removed source cannot begin another native drain."),
        };
    }

    public MonitoringBackupHistory Claim(BackupLease lease) => this switch
    {
        MonitoringBackupHistory monitoring => monitoring with
        {
            Revision = Revision + 1,
            Dispatch = new ClaimedBackupDispatch(monitoring.Dispatch.NextAttemptAtUtc, ExecutionOrigin, lease)
        },
        QuiescentBackupHistory or StoppedBackupHistory => throw new InvalidOperationException(
            "Terminal history cannot be claimed."),
    };

    public MonitoringBackupHistory Retry(
        DateTime utcNow,
        TimeSpan delay,
        string failure) => this switch
    {
        MonitoringBackupHistory monitoring => monitoring with
        {
            Revision = Revision + 1, Dispatch = new AvailableBackupDispatch(utcNow + delay, ExecutionOrigin),
            LastFailure = failure
        },
        QuiescentBackupHistory or StoppedBackupHistory => throw new InvalidOperationException(
            "Terminal history cannot retry."),
    };

    public MonitoringBackupHistory Restart(BackupHistoryStart start, DateTime utcNow) => this switch
    {
        MonitoringBackupHistory monitoring => monitoring with
        {
            Revision = Revision + 1, Checkpoint = InitialCheckpoint(start.ScanId),
            Dispatch = new AvailableBackupDispatch(utcNow, start.Origin), LastFailure = null
        },
        QuiescentBackupHistory or StoppedBackupHistory => throw new InvalidOperationException(
            "Only active observation can restart its snapshot."),
    };

    public BackupArchiveHistory Accept(
        BackupHistoryPosition position,
        IReadOnlyList<BackupNativeActivity> activities,
        DateTime utcNow,
        TimeSpan continuationDelay)
    {
        if (this is not MonitoringBackupHistory monitoring)
            throw new InvalidOperationException("Terminal history cannot accept another batch.");
        if (monitoring.Mode is PreparingBackupHistoryDrain)
            throw new InvalidOperationException("A drain must suspend its schedule before accepting history.");
        if (position is InitialBackupHistoryPosition)
            throw new ArgumentException("An accepted batch must report its position.", nameof(position));
        var nextStream = position switch
        {
            PagedBackupHistoryPosition page => page.Stream,
            FinishingBackupHistoryPosition finishing => finishing.Stream,
            FollowingBackupHistoryPosition followed => followed.Stream,
            InitialBackupHistoryPosition => throw new ArgumentException(
                "An accepted batch must report its stream.",
                nameof(position)),
        };
        ArgumentException.ThrowIfNullOrWhiteSpace(nextStream.PublisherId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nextStream.Version);
        switch (monitoring.Checkpoint.Position)
        {
            case PagedBackupHistoryPosition previous when previous.Stream != nextStream:
                throw new InvalidOperationException("A paged snapshot must retain its publisher and version.");
            case FollowingBackupHistoryPosition previous when previous.Stream.PublisherId != nextStream.PublisherId:
                throw new InvalidOperationException("Changing the publisher requires a new snapshot.");
            case FollowingBackupHistoryPosition when position is PagedBackupHistoryPosition:
                throw new InvalidOperationException("A backfill requires a new scan epoch.");
        }

        if (monitoring.Mode is DrainingBackupHistory mode && mode.Suspension.PublisherId != nextStream.PublisherId)
            throw new InvalidOperationException("A drain cannot observe another native publisher.");

        var active = monitoring.Checkpoint.ActiveNativeIds;
        foreach (var activity in activities)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(activity.NativeUid);
            active = activity.InUse ? active.Add(activity.NativeUid) : active.Remove(activity.NativeUid);
        }

        if (monitoring.Mode is DrainingBackupHistory drain && position is FollowingBackupHistoryPosition following &&
            active.Count == 0)
        {
            if (following.Stream.PublisherId != drain.Suspension.PublisherId)
                throw new InvalidOperationException("A drain cannot complete for another native publisher.");
            return new QuiescentBackupHistory(Revision + 1, drain.Suspension, following.Stream, ExecutionOrigin);
        }

        return monitoring with
        {
            Revision = Revision + 1,
            Checkpoint = monitoring.Checkpoint with { Position = position, ActiveNativeIds = active },
            Dispatch = new AvailableBackupDispatch(utcNow + continuationDelay, ExecutionOrigin), LastFailure = null
        };
    }

    public StoppedBackupHistory Stop(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return this switch
        {
            StoppedBackupHistory stopped => stopped,
            MonitoringBackupHistory or QuiescentBackupHistory => new StoppedBackupHistory(
                Revision + 1,
                reason,
                ExecutionOrigin),
        };
    }

    private static BackupHistoryCheckpoint InitialCheckpoint(string scanId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scanId);
        return new(scanId, new InitialBackupHistoryPosition(), ImmutableHashSet.Create<string>(StringComparer.Ordinal));
    }
}

public sealed record MonitoringBackupHistory(
    long Revision,
    BackupHistoryMode Mode,
    BackupHistoryCheckpoint Checkpoint,
    BackupDispatch Dispatch,
    string? LastFailure
) : BackupArchiveHistory(Revision);

public sealed record QuiescentBackupHistory(
    long Revision,
    BackupScheduleSuspension Suspension,
    BackupHistoryStream Stream,
    BackupExecutionOrigin Origin
) : BackupArchiveHistory(Revision);

public sealed record StoppedBackupHistory(
    long Revision,
    string Reason,
    BackupExecutionOrigin Origin
) : BackupArchiveHistory(Revision);
