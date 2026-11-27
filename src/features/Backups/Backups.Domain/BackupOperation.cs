// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.Backups.Domain;

public enum BackupOperationKind { Backup, Restore, ScheduledBackup }

public enum BackupOperationState { Pending, Running, Succeeded, Failed, Interrupted }

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(BackupCopySelection), typeof(BackupTimeSelection))]
public closed record BackupRestoreSelection;

public sealed record BackupCopySelection(
    string BackupId
) : BackupRestoreSelection;

public sealed record BackupTimeSelection(
    [property: BsonDateTimeOptions(Representation = BsonType.Document)]
    DateTime TargetTimeUtc
) : BackupRestoreSelection;

public sealed record BackupRestoreResource(
    string Id,
    string Name,
    int? StorageGiB
);

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(AwaitingRestoreTarget), typeof(RecordedRestoreTarget))]
public closed record BackupRestoreTarget;

public sealed record AwaitingRestoreTarget : BackupRestoreTarget;

public sealed record RecordedRestoreTarget(
    string ResourceId
) : BackupRestoreTarget;

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(RequestedBackupCopy),
    typeof(RunningBackupCopy),
    typeof(CompletedBackupCopy),
    typeof(FailedBackupCopy))]
public closed record BackupCopyExecution;

public sealed record RequestedBackupCopy(
    BackupDispatch Dispatch
) : BackupCopyExecution;

public sealed record RunningBackupCopy(
    BackupDispatch Dispatch
) : BackupCopyExecution;

public sealed record CompletedBackupCopy(
    string BackupId
) : BackupCopyExecution;

public sealed record FailedBackupCopy(
    string Reason
) : BackupCopyExecution;

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(RequestedBackupRestore),
    typeof(ProvisioningBackupRestore),
    typeof(CompletedBackupRestore),
    typeof(FailedBackupRestore))]
public closed record BackupRestoreExecution;

public sealed record RequestedBackupRestore(
    BackupDispatch Dispatch
) : BackupRestoreExecution;

public sealed record ProvisioningBackupRestore(
    RecordedRestoreTarget Target,
    BackupDispatch Dispatch
) : BackupRestoreExecution;

public sealed record CompletedBackupRestore(
    RecordedRestoreTarget Target
) : BackupRestoreExecution;

public sealed record FailedBackupRestore(
    string Reason,
    BackupRestoreTarget Target
) : BackupRestoreExecution;

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(RunningScheduledBackup),
    typeof(CompletedScheduledBackup),
    typeof(FailedScheduledBackup),
    typeof(InterruptedScheduledBackup))]
public closed record ScheduledBackupExecution;

public sealed record RunningScheduledBackup : ScheduledBackupExecution;

public sealed record CompletedScheduledBackup(
    string BackupId
) : ScheduledBackupExecution;

public sealed record FailedScheduledBackup(
    string Reason
) : ScheduledBackupExecution;

public sealed record InterruptedScheduledBackup(
    string Reason
) : ScheduledBackupExecution;

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(ManualBackupWork), typeof(ScheduledBackupWork), typeof(RestoreBackupWork))]
public closed record BackupOperationWork;

public sealed record ManualBackupWork(
    BackupCopyExecution Execution
) : BackupOperationWork;

public sealed record ScheduledBackupWork(
    string NativeUid,
    long ObservedPolicyRevision,
    string ScanId,
    ScheduledBackupExecution Execution
) : BackupOperationWork;

public sealed record RestoreBackupWork(
    BackupRestoreSelection Selection,
    BackupRestoreResource Resource,
    BackupRestoreExecution Execution
) : BackupOperationWork;

public sealed record BackupOperation(
    string Id,
    string TenantId,
    string IdempotencyKey,
    string PolicyId,
    string ArchiveId,
    BackupOperationWork Work,
    BackupExecutionOrigin Origin,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? LastAttemptFailure
) : IBackupDocument
{
    [BsonIgnore]
    public BackupOperationKind Kind =>
        Work switch
        {
            ManualBackupWork => BackupOperationKind.Backup,
            ScheduledBackupWork => BackupOperationKind.ScheduledBackup,
            RestoreBackupWork => BackupOperationKind.Restore,
        };

    [BsonIgnore]
    public BackupOperationState State =>
        Work switch
        {
            ManualBackupWork { Execution: RequestedBackupCopy } => BackupOperationState.Pending,
            ManualBackupWork { Execution: RunningBackupCopy } => BackupOperationState.Running,
            ManualBackupWork { Execution: CompletedBackupCopy } => BackupOperationState.Succeeded,
            ManualBackupWork { Execution: FailedBackupCopy } => BackupOperationState.Failed,
            ScheduledBackupWork { Execution: RunningScheduledBackup } => BackupOperationState.Running,
            ScheduledBackupWork { Execution: CompletedScheduledBackup } => BackupOperationState.Succeeded,
            ScheduledBackupWork { Execution: FailedScheduledBackup } => BackupOperationState.Failed,
            ScheduledBackupWork { Execution: InterruptedScheduledBackup } => BackupOperationState.Interrupted,
            RestoreBackupWork { Execution: RequestedBackupRestore } => BackupOperationState.Pending,
            RestoreBackupWork { Execution: ProvisioningBackupRestore } => BackupOperationState.Running,
            RestoreBackupWork { Execution: CompletedBackupRestore } => BackupOperationState.Succeeded,
            RestoreBackupWork { Execution: FailedBackupRestore } => BackupOperationState.Failed,
        };

    [BsonIgnore]
    public BackupDispatch? Dispatch =>
        Work switch
        {
            ManualBackupWork { Execution: RequestedBackupCopy requested } => requested.Dispatch,
            ManualBackupWork { Execution: RunningBackupCopy running } => running.Dispatch,
            RestoreBackupWork { Execution: RequestedBackupRestore requested } => requested.Dispatch,
            RestoreBackupWork { Execution: ProvisioningBackupRestore provisioning } => provisioning.Dispatch,
            ManualBackupWork { Execution: CompletedBackupCopy or FailedBackupCopy } => null,
            RestoreBackupWork { Execution: CompletedBackupRestore or FailedBackupRestore } => null,
            ScheduledBackupWork => null,
        };

    [BsonIgnore]
    public string? FailureCode =>
        Work switch
        {
            ManualBackupWork { Execution: FailedBackupCopy failed } => failed.Reason,
            ScheduledBackupWork { Execution: FailedScheduledBackup failed } => failed.Reason,
            ScheduledBackupWork { Execution: InterruptedScheduledBackup interrupted } => interrupted.Reason,
            RestoreBackupWork { Execution: FailedBackupRestore failed } => failed.Reason,
            ManualBackupWork
            {
                Execution: RequestedBackupCopy or RunningBackupCopy or CompletedBackupCopy
            } => LastAttemptFailure,
            ScheduledBackupWork { Execution: RunningScheduledBackup or CompletedScheduledBackup } => null,
            RestoreBackupWork
            {
                Execution: RequestedBackupRestore or ProvisioningBackupRestore or CompletedBackupRestore
            } => LastAttemptFailure,
        };

    public static BackupOperation RequestCopy(
        string id,
        string tenant,
        string key,
        string policy,
        string archive,
        DateTime utcNow,
        BackupExecutionOrigin origin) => new(
        id,
        tenant,
        key,
        policy,
        archive,
        new ManualBackupWork(new RequestedBackupCopy(NewDispatch(utcNow, origin))),
        origin,
        utcNow,
        utcNow,
        null);

    public static BackupOperation RequestRestore(
        string id,
        string tenant,
        string key,
        string policy,
        string archive,
        BackupRestoreSelection selection,
        BackupRestoreResource resource,
        DateTime utcNow,
        BackupExecutionOrigin origin)
    {
        switch (selection)
        {
            case BackupCopySelection copy:
                ArgumentException.ThrowIfNullOrWhiteSpace(copy.BackupId);
                break;
            case BackupTimeSelection time when time.TargetTimeUtc.Kind != DateTimeKind.Utc:
                throw new ArgumentException("The recovery target must be UTC.", nameof(selection));
            case BackupTimeSelection:
                break;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Name);
        if (resource.StorageGiB <= 0)
            throw new ArgumentOutOfRangeException(nameof(resource));
        return new BackupOperation(
            id,
            tenant,
            key,
            policy,
            archive,
            new RestoreBackupWork(selection, resource, new RequestedBackupRestore(NewDispatch(utcNow, origin))),
            origin,
            utcNow,
            utcNow,
            null);
    }

    public static BackupOperation ObserveScheduled(
        string id,
        string tenant,
        string key,
        string policy,
        string archive,
        string nativeUid,
        long observedPolicyRevision,
        string scanId,
        ScheduledBackupExecution execution,
        DateTime startedAt,
        DateTime updatedAt,
        BackupExecutionOrigin origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeUid);
        switch (execution)
        {
            case CompletedScheduledBackup completed:
                ArgumentException.ThrowIfNullOrWhiteSpace(completed.BackupId);
                break;
            case FailedScheduledBackup failed:
                ArgumentException.ThrowIfNullOrWhiteSpace(failed.Reason);
                break;
            case InterruptedScheduledBackup interrupted:
                ArgumentException.ThrowIfNullOrWhiteSpace(interrupted.Reason);
                break;
            case RunningScheduledBackup:
                break;
        }

        return new BackupOperation(
            id,
            tenant,
            key,
            policy,
            archive,
            new ScheduledBackupWork(nativeUid, observedPolicyRevision, scanId, execution),
            origin,
            startedAt,
            updatedAt,
            null);
    }

    public BackupOperation Claim(BackupLease lease) => WithDispatch(
        new ClaimedBackupDispatch(RequiredDispatch().NextAttemptAtUtc, RequiredDispatch().Origin, lease));

    public BackupOperation Retry(
        DateTime utcNow,
        TimeSpan delay,
        string? failure) => WithDispatch(new AvailableBackupDispatch(utcNow + delay, RequiredDispatch().Origin)) with
    {
        UpdatedAt = utcNow, LastAttemptFailure = failure
    };

    public BackupOperation CopyRunning(DateTime utcNow, TimeSpan delay)
    {
        var dispatch = new AvailableBackupDispatch(utcNow + delay, RequiredDispatch().Origin);
        return Work switch
        {
            ManualBackupWork { Execution: RequestedBackupCopy or RunningBackupCopy } => this with
            {
                Work = new ManualBackupWork(new RunningBackupCopy(dispatch)), UpdatedAt = utcNow,
                LastAttemptFailure = null
            },
            ManualBackupWork { Execution: CompletedBackupCopy or FailedBackupCopy } or RestoreBackupWork
                or ScheduledBackupWork => throw new InvalidOperationException(
                    "Only an active manual backup can start running."),
        };
    }

    public BackupOperation RecordRestoreTarget(string resourceId, DateTime utcNow) => Work switch
    {
        RestoreBackupWork { Resource.Id: var expected, Execution: RequestedBackupRestore requested } restore
            when expected == resourceId => this with
            {
                Work = restore with
                {
                    Execution = new ProvisioningBackupRestore(new RecordedRestoreTarget(resourceId), requested.Dispatch)
                },
                UpdatedAt = utcNow
            },
        ManualBackupWork or ScheduledBackupWork or RestoreBackupWork => throw new InvalidOperationException(
            "The restore target is not awaiting creation."),
    };

    public BackupOperation CompleteCopy(string backupId, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupId);
        return Work switch
        {
            ManualBackupWork { Execution: RequestedBackupCopy or RunningBackupCopy } => this with
            {
                Work = new ManualBackupWork(new CompletedBackupCopy(backupId)), UpdatedAt = utcNow,
                LastAttemptFailure = null
            },
            ManualBackupWork { Execution: CompletedBackupCopy or FailedBackupCopy } or RestoreBackupWork
                or ScheduledBackupWork => throw new InvalidOperationException(
                    "Only an active manual backup can complete."),
        };
    }

    public BackupOperation CompleteRestore(DateTime utcNow) => Work switch
    {
        RestoreBackupWork { Execution: ProvisioningBackupRestore provisioning } restore => this with
        {
            Work = restore with { Execution = new CompletedBackupRestore(provisioning.Target) }, UpdatedAt = utcNow,
            LastAttemptFailure = null
        },
        RestoreBackupWork { Execution: RequestedBackupRestore or CompletedBackupRestore or FailedBackupRestore }
            or ManualBackupWork
            or ScheduledBackupWork =>
            throw new InvalidOperationException("Restore success requires a recorded target."),
    };

    public BackupOperation Fail(string reason, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        BackupOperationWork work = Work switch
        {
            ManualBackupWork { Execution: RequestedBackupCopy or RunningBackupCopy } => new ManualBackupWork(
                new FailedBackupCopy(reason)),
            RestoreBackupWork { Execution: RequestedBackupRestore } restore => restore with
            {
                Execution = new FailedBackupRestore(reason, new AwaitingRestoreTarget())
            },
            RestoreBackupWork { Execution: ProvisioningBackupRestore provisioning } restore => restore with
            {
                Execution = new FailedBackupRestore(reason, provisioning.Target)
            },
            ManualBackupWork { Execution: CompletedBackupCopy or FailedBackupCopy } or RestoreBackupWork
            {
                Execution: CompletedBackupRestore or FailedBackupRestore
            } or ScheduledBackupWork => throw new InvalidOperationException(
                "A terminal or scheduled operation cannot be failed by dispatch."),
        };
        return this with { Work = work, UpdatedAt = utcNow, LastAttemptFailure = null };
    }

    public BackupOperation InterruptObservation(string reason, DateTime utcNow) => Work switch
    {
        ScheduledBackupWork { Execution: RunningScheduledBackup } scheduled => this with
        {
            Work = scheduled with { Execution = new InterruptedScheduledBackup(reason) }, UpdatedAt = utcNow
        },
        ScheduledBackupWork
            {
                Execution: CompletedScheduledBackup or FailedScheduledBackup or InterruptedScheduledBackup
            } or ManualBackupWork
            or RestoreBackupWork => throw new InvalidOperationException(
                "Only a running scheduled backup can lose observation."),
    };

    private BackupDispatch RequiredDispatch() => Dispatch ??
        throw new InvalidOperationException("A terminal or scheduled operation cannot be dispatched.");

    private BackupOperation WithDispatch(BackupDispatch dispatch) => Work switch
    {
        ManualBackupWork { Execution: RequestedBackupCopy } manual => this with
        {
            Work = manual with { Execution = new RequestedBackupCopy(dispatch) }
        },
        ManualBackupWork { Execution: RunningBackupCopy } manual => this with
        {
            Work = manual with { Execution = new RunningBackupCopy(dispatch) }
        },
        RestoreBackupWork { Execution: RequestedBackupRestore } restore => this with
        {
            Work = restore with { Execution = new RequestedBackupRestore(dispatch) }
        },
        RestoreBackupWork { Execution: ProvisioningBackupRestore provisioning } restore => this with
        {
            Work = restore with { Execution = provisioning with { Dispatch = dispatch } }
        },
        ManualBackupWork { Execution: CompletedBackupCopy or FailedBackupCopy } or RestoreBackupWork
        {
            Execution: CompletedBackupRestore or FailedBackupRestore
        } or ScheduledBackupWork => throw new InvalidOperationException(
            "A terminal or scheduled operation cannot be dispatched."),
    };

    private static AvailableBackupDispatch NewDispatch(DateTime utcNow, BackupExecutionOrigin origin) =>
        new(utcNow, origin);
}
