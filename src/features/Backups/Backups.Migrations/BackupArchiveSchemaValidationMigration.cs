// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;
using static Pole.Features.Backups.Migrations.BackupCollectionSchema;

namespace Pole.Features.Backups.Migrations;

public sealed class BackupArchiveSchemaValidationMigration : IMigration
{
    public ObjectId Id => new("6af5a10031800609592fefa3");
    public string Name => "Backup archive document validation";
    public string Description => "Validate retained archive context and incremental native history checkpoints.";

    public Task Up(IMongoDatabase context, CancellationToken cancellationToken = default) => ApplyAsync(
        context,
        "BackupArchives",
        Object(
            ("_id", Text()),
            ("TenantId", Text()),
            ("PolicyId", Text()),
            ("DestinationId", Text()),
            ("ResourceType", Text()),
            ("ResourceId", Text()),
            ("ResourceName", Text()),
            ("SourceRevision", Number("long", 1)),
            ("ServiceVersion", Text()),
            ("StorageGiB", Number("int", 1)),
            ("Instances", Number("int", 1)),
            ("Storage", Storage()),
            ("Placement",
                Object(
                    ("ResourceGroupId", Text()),
                    ("IsolationScopeId", Text()),
                    ("DeploymentTargetId", Text()),
                    ("RuntimeCellId", Text()),
                    ("NamespaceName", Text()),
                    ("SourceDeploymentId", Text()),
                    ("CredentialDeploymentId", Text()),
                    ("StorageProfileId", Text()),
                    ("StorageProfileRevision", Number("long", 1)),
                    ("ComputeProfileId", Text()),
                    ("ComputeProfileRevision", Number("long", 1)))),
            ("ServerName", Text()),
            ("Active", Type("bool")),
            ("CreatedAt", Type("date")),
            ("ReferenceFence", Number("long", 0)),
            ("History", History())),
        cancellationToken);

    private static BsonDocument History() => OneOf(
        Variant(
            "BackupArchiveHistory",
            "MonitoringBackupHistory",
            ("Revision", Number("long", 1)),
            ("Mode",
                OneOf(
                    Variant("BackupHistoryMode", "LiveBackupHistory"),
                    Variant("BackupHistoryMode", "PreparingBackupHistoryDrain", ("PolicyRevision", Number("long", 1))),
                    Variant("BackupHistoryMode", "DrainingBackupHistory", ("Suspension", Suspension())))),
            ("Checkpoint", Object(("ScanId", Text()), ("Position", Position()), ("ActiveNativeIds", Strings()))),
            ("Dispatch", Dispatch()),
            ("LastFailure", Nullable(Text()))),
        Variant(
            "BackupArchiveHistory",
            "QuiescentBackupHistory",
            ("Revision", Number("long", 1)),
            ("Suspension", Suspension()),
            ("Stream", Stream()),
            ("Origin", Origin())),
        Variant(
            "BackupArchiveHistory",
            "StoppedBackupHistory",
            ("Revision", Number("long", 1)),
            ("Reason", Text()),
            ("Origin", Origin())));

    private static BsonDocument Position() => OneOf(
        Variant("BackupHistoryPosition", "InitialBackupHistoryPosition"),
        Variant("BackupHistoryPosition", "PagedBackupHistoryPosition", ("Stream", Stream()), ("Continuation", Text())),
        Variant("BackupHistoryPosition", "FinishingBackupHistoryPosition", ("Stream", Stream())),
        Variant("BackupHistoryPosition", "FollowingBackupHistoryPosition", ("Stream", Stream())));
}
