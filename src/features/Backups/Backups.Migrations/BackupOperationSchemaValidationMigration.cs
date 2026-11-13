using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;
using static Pole.Features.Backups.Migrations.BackupCollectionSchema;

namespace Pole.Features.Backups.Migrations;

public sealed class BackupOperationSchemaValidationMigration : IMigration
{
    public ObjectId Id => new("6af5a10031800609592fefa4");
    public string Name => "Backup operation document validation";
    public string Description => "Require variant-specific execution, successful identities and precise PITR targets.";

    public Task Up(IMongoDatabase context, CancellationToken cancellationToken = default) => ApplyAsync(
        context,
        "BackupOperations",
        Object(
            ("_id", Text()),
            ("TenantId", Text()),
            ("IdempotencyKey", Text()),
            ("PolicyId", Text()),
            ("ArchiveId", Text()),
            ("Work",
                OneOf(
                    Variant("BackupOperationWork", "ManualBackupWork", ("Execution", CopyExecution())),
                    Variant(
                        "BackupOperationWork",
                        "ScheduledBackupWork",
                        ("NativeUid", Text()),
                        ("ObservedPolicyRevision", Number("long", 1)),
                        ("ScanId", Text()),
                        ("Execution", ScheduledExecution())),
                    Variant(
                        "BackupOperationWork",
                        "RestoreBackupWork",
                        ("Selection", Selection()),
                        ("Resource",
                            Object(("_id", Text()), ("Name", Text()), ("StorageGiB", Nullable(Number("int", 1))))),
                        ("Execution", RestoreExecution())))),
            ("Origin", Origin()),
            ("CreatedAt", Type("date")),
            ("UpdatedAt", Type("date")),
            ("LastAttemptFailure", Nullable(Text()))),
        cancellationToken);

    private static BsonDocument CopyExecution() => OneOf(
        Variant("BackupCopyExecution", "RequestedBackupCopy", ("Dispatch", Dispatch())),
        Variant("BackupCopyExecution", "RunningBackupCopy", ("Dispatch", Dispatch())),
        Variant("BackupCopyExecution", "CompletedBackupCopy", ("BackupId", Text())),
        Variant("BackupCopyExecution", "FailedBackupCopy", ("Reason", Text())));

    private static BsonDocument ScheduledExecution() => OneOf(
        Variant("ScheduledBackupExecution", "RunningScheduledBackup"),
        Variant("ScheduledBackupExecution", "CompletedScheduledBackup", ("BackupId", Text())),
        Variant("ScheduledBackupExecution", "FailedScheduledBackup", ("Reason", Text())),
        Variant("ScheduledBackupExecution", "InterruptedScheduledBackup", ("Reason", Text())));

    private static BsonDocument Selection() => OneOf(
        Variant("BackupRestoreSelection", "BackupCopySelection", ("BackupId", Text())),
        Variant(
            "BackupRestoreSelection",
            "BackupTimeSelection",
            ("TargetTimeUtc", Object(("DateTime", Type("date")), ("Ticks", Number("long", 0))))));

    private static BsonDocument RecordedTarget() => Variant(
        "BackupRestoreTarget",
        "RecordedRestoreTarget",
        ("ResourceId", Text()));

    private static BsonDocument RestoreExecution() => OneOf(
        Variant("BackupRestoreExecution", "RequestedBackupRestore", ("Dispatch", Dispatch())),
        Variant(
            "BackupRestoreExecution",
            "ProvisioningBackupRestore",
            ("Target", RecordedTarget()),
            ("Dispatch", Dispatch())),
        Variant("BackupRestoreExecution", "CompletedBackupRestore", ("Target", RecordedTarget())),
        Variant(
            "BackupRestoreExecution",
            "FailedBackupRestore",
            ("Reason", Text()),
            ("Target", OneOf(Variant("BackupRestoreTarget", "AwaitingRestoreTarget"), RecordedTarget()))));
}
