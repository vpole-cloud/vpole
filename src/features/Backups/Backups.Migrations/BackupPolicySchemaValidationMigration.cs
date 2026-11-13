using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;
using static Pole.Features.Backups.Migrations.BackupCollectionSchema;

namespace Pole.Features.Backups.Migrations;

public sealed class BackupPolicySchemaValidationMigration : IMigration
{
    public ObjectId Id => new("6af5a10031800609592fefa2");
    public string Name => "Backup policy document validation";
    public string Description => "Validate policy lifecycle, durable dispatch and native drain authority.";

    public Task Up(IMongoDatabase context, CancellationToken cancellationToken = default) => ApplyAsync(
        context,
        "BackupPolicies",
        Object(
            ("_id", Text()),
            ("TenantId", Text()),
            ("Name", Text()),
            ("ResourceType", Text()),
            ("ResourceId", Text()),
            ("DestinationId", Text()),
            ("ArchiveId", Text()),
            ("Schedule", Text()),
            ("Suspended", Type("bool")),
            ("RetentionDays", Number("int", 1)),
            ("Revision", Number("long", 1)),
            ("AppliedRevision", Number("long", 0)),
            ("FailureCode", Nullable(Text())),
            ("Lifecycle",
                OneOf(
                    Variant("BackupPolicyLifecycle", "ActiveBackupPolicy"),
                    Variant("BackupPolicyLifecycle", "StoppingBackupPolicy"))),
            ("Dispatch", Dispatch()),
            ("PlanningFence", Number("long", 0)),
            ("Observation",
                Nullable(
                    Object(
                        ("WalArchivingStatus", Text()),
                        ("WalStatusChangedAt", Nullable(Type("date"))),
                        ("LastSuccessfulBackupAt", Nullable(Type("date"))),
                        ("NextScheduledAt", Nullable(Type("date")))))),
            ("Application",
                OneOf(
                    Variant("BackupPolicyApplication", "ApplyingBackupPolicy"),
                    Variant(
                        "BackupPolicyApplication",
                        "DrainingBackupPolicy",
                        ("ArchiveId", Text()),
                        ("Suspension", Suspension()))))),
        cancellationToken);
}
