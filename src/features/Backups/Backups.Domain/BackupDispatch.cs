using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.Backups.Domain;

public sealed record BackupExecutionOrigin(
    string TraceId,
    string CorrelationId
);

public sealed record BackupLease(
    string Id,
    DateTime ExpiresAtUtc
);

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(AvailableBackupDispatch), typeof(ClaimedBackupDispatch))]
public closed record BackupDispatch(
    DateTime NextAttemptAtUtc,
    BackupExecutionOrigin Origin
);

public sealed record AvailableBackupDispatch(
    DateTime NextAttemptAtUtc,
    BackupExecutionOrigin Origin
) : BackupDispatch(NextAttemptAtUtc, Origin);

public sealed record ClaimedBackupDispatch(
    DateTime NextAttemptAtUtc,
    BackupExecutionOrigin Origin,
    BackupLease Lease
) : BackupDispatch(NextAttemptAtUtc, Origin);
