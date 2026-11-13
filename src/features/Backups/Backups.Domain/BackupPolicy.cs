using MongoDB.Bson.Serialization.Attributes;
using Pole.Scheduling;

namespace Pole.Features.Backups.Domain;

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(ActiveBackupPolicy), typeof(StoppingBackupPolicy))]
public closed record BackupPolicyLifecycle;

public sealed record ActiveBackupPolicy : BackupPolicyLifecycle;

public sealed record StoppingBackupPolicy : BackupPolicyLifecycle;

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(ApplyingBackupPolicy), typeof(DrainingBackupPolicy))]
public closed record BackupPolicyApplication;

public sealed record ApplyingBackupPolicy : BackupPolicyApplication;

public sealed record DrainingBackupPolicy(
    string ArchiveId,
    BackupScheduleSuspension Suspension
) : BackupPolicyApplication;

public sealed record BackupPolicyScheduleReport(
    DateTime? NextScheduledAt,
    DateTime? LastSuccessfulBackupAt,
    string? WalArchivingStatus
);

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
    BackupPolicyLifecycle Lifecycle,
    BackupDispatch Dispatch,
    long PlanningFence,
    BackupObservation? Observation,
    BackupPolicyApplication Application
) : IBackupConfigurationDocument
{
    [BsonIgnore] public bool Deleting => Lifecycle is StoppingBackupPolicy;

    public BackupPolicyScheduleReport ReportSchedule(DateTimeOffset now)
    {
        DateTime? next = null;
        if (!Suspended && !Deleting && AppliedRevision == Revision && FailureCode is null &&
            Dispatch is AvailableBackupDispatch && Observation is { NativeScheduleEnabled: true })
            next = Observation?.NextScheduledAt is { } observed && observed > now.UtcDateTime
                ? observed
                : UtcCronSchedule.Parse(Schedule).FindNextOccurrence(now)?.UtcDateTime;

        return new BackupPolicyScheduleReport(
            next,
            Observation?.LastSuccessfulBackupAt,
            Observation?.WalArchivingStatus);
    }

    public BackupPolicy Reconfigure(
        string destinationId,
        string archiveId,
        string schedule,
        bool suspended,
        int retentionDays,
        BackupExecutionOrigin origin,
        DateTime utcNow)
    {
        if (Lifecycle is StoppingBackupPolicy)
            throw new InvalidOperationException("A stopping backup policy cannot be reconfigured.");
        BackupPolicyFactory.ValidateSchedule(schedule, retentionDays, utcNow);
        if (DestinationId == destinationId && ArchiveId == archiveId && Schedule == schedule &&
            Suspended == suspended && RetentionDays == retentionDays)
            return this;

        return this with
        {
            DestinationId = destinationId,
            ArchiveId = archiveId,
            Schedule = schedule,
            Suspended = suspended,
            RetentionDays = retentionDays,
            Revision = checked(Revision + 1),
            FailureCode = null,
            Dispatch = new AvailableBackupDispatch(utcNow, origin),
            Observation = ArchiveId == archiveId ? Observation : null,
            Application = new ApplyingBackupPolicy(),
        };
    }

    public BackupPolicy BeginStopping(BackupExecutionOrigin origin, DateTime utcNow) => Lifecycle switch
    {
        ActiveBackupPolicy => this with
        {
            Lifecycle = new StoppingBackupPolicy(),
            Revision = checked(Revision + 1),
            FailureCode = null,
            Dispatch = new AvailableBackupDispatch(utcNow, origin),
            Application = new ApplyingBackupPolicy(),
        },
        StoppingBackupPolicy => this,
    };

    public BackupPolicy Claim(BackupLease lease) => this with
    {
        Dispatch = new ClaimedBackupDispatch(Dispatch.NextAttemptAtUtc, Dispatch.Origin, lease),
    };
}
