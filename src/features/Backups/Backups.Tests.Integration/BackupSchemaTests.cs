using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Pole.Features.Backups.Domain;
using Pole.Testing;
using Xunit;

namespace Pole.Features.Backups.Tests.Integration;

[Collection("backup-mongo")]
public sealed class BackupSchemaTests(
    MongoReplicaSetFixture mongo
) : IAsyncLifetime
{
    private readonly BackupTestSetup setup = new(mongo);
    private static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
    private static readonly BackupExecutionOrigin Origin = new("schema-trace", "schema-correlation");

    public Task InitializeAsync() => mongo.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("BackupDestinations", "Storage")]
    [InlineData("BackupPolicies", "TenantId")]
    [InlineData("BackupArchives", "History")]
    [InlineData("BackupOperations", "Work")]
    public async Task Every_collection_installs_strict_validation_and_rejects_an_incomplete_document(
        string name,
        string field)
    {
        await using var host = await setup.StartAsync();
        var document = name switch
        {
            "BackupDestinations" => Destination().ToBsonDocument(),
            "BackupPolicies" => Policy().ToBsonDocument(),
            "BackupArchives" => Archive().ToBsonDocument(),
            "BackupOperations" => Copy().ToBsonDocument(),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
        var collection = mongo.Database.GetCollection<BsonDocument>(name);
        await collection.InsertOneAsync(document);
        var invalid = document.DeepClone().AsBsonDocument;
        invalid["_id"] = Id();
        if (invalid.Contains("Name"))
            invalid["Name"] = Id();
        if (name == "BackupPolicies")
            invalid["ResourceId"] = Id();
        if (name == "BackupOperations")
            invalid["IdempotencyKey"] = Id();
        invalid.Remove(field);
        await RejectedAsync(collection, invalid);
        using var metadata = await mongo.Database.ListCollectionsAsync(
            new ListCollectionsOptions { Filter = new BsonDocument("name", name) });
        var options = (await metadata.ToListAsync()).Single()["options"].AsBsonDocument;
        Assert.Equal("strict", options["validationLevel"].AsString);
        Assert.Equal("error", options["validationAction"].AsString);
        Assert.True(options["validator"]["$jsonSchema"].AsBsonDocument.Contains("properties"));
    }

    [Fact]
    public async Task Actual_execution_variants_and_cohesive_claims_are_accepted_by_the_frozen_schema()
    {
        await using var host = await setup.StartAsync();
        var copy = Copy();
        var copyStates = new[]
        {
            copy, copy.Claim(new("lease", Now.AddMinutes(1))), copy.CopyRunning(Now, TimeSpan.Zero),
            copy.CompleteCopy("backup", Now), copy.Fail("failure", Now),
        };
        foreach (var operation in copyStates)
            await PersistAsync("BackupOperations", operation);
        var restore = BackupOperation.RequestRestore(
            Id(),
            TestPlatformDefaults.TenantId,
            "restore",
            "policy",
            "archive",
            new BackupCopySelection("copy"),
            new(Id(), "restored", null),
            Now,
            Origin);
        var target = restore.RecordRestoreTarget(((RestoreBackupWork)restore.Work).Resource.Id, Now);
        foreach (var operation in new[]
                 {
                     restore, target, target.CompleteRestore(Now), restore.Fail("before-create", Now),
                     target.Fail("after-create", Now)
                 })
            await PersistAsync("BackupOperations", operation);
        foreach (ScheduledBackupExecution execution in new ScheduledBackupExecution[]
                 {
                     new RunningScheduledBackup(), new CompletedScheduledBackup("copy"),
                     new FailedScheduledBackup("failed"), new InterruptedScheduledBackup("removed")
                 })
            await PersistAsync(
                "BackupOperations",
                BackupOperation.ObserveScheduled(
                    Id(),
                    TestPlatformDefaults.TenantId,
                    Id(),
                    "policy",
                    "archive",
                    Id(),
                    1,
                    "scan",
                    execution,
                    Now,
                    Now,
                    Origin));
    }

    [Fact]
    public async Task All_history_positions_modes_and_terminal_states_match_the_frozen_archive_shape()
    {
        await using var host = await setup.StartAsync();
        var archive = Archive();
        var history = archive.History;
        var fence = new BackupScheduleSuspension(archive.PolicyId, 2, "publisher");
        var page = history.Accept(
            new PagedBackupHistoryPosition(new("publisher", "opaque"), "next"),
            [new("native", true)],
            Now,
            TimeSpan.Zero);
        var following = page.Accept(
            new FollowingBackupHistoryPosition(new("publisher", "opaque")),
            [],
            Now,
            TimeSpan.Zero);
        var draining = history.RequestDrain(fence, new("post-pause", Origin), Now);
        var states = new[]
        {
            history, history.Claim(new("lease", Now.AddMinutes(1))),
            history.PrepareDrain(2, new("prepare", Origin), Now),
            page, following,
            page.Accept(new FinishingBackupHistoryPosition(new("publisher", "opaque")), [], Now, TimeSpan.Zero),
            draining,
            draining.Accept(new FollowingBackupHistoryPosition(new("publisher", "opaque")), [], Now, TimeSpan.Zero),
            history.Stop("source-removed"),
        };
        foreach (var state in states)
            await PersistAsync("BackupArchives", archive with { History = state });
        var policy = Policy();
        await PersistAsync("BackupPolicies", policy);
        await PersistAsync("BackupPolicies", policy.BeginStopping(Origin, Now));
        await PersistAsync("BackupPolicies", policy with { Application = new DrainingBackupPolicy(archive.Id, fence) });
    }

    [Theory]
    [InlineData("completed-copy-without-id")]
    [InlineData("completed-restore-without-recorded-target")]
    [InlineData("available-claim-with-lease")]
    public async Task Invalid_variant_combinations_are_rejected_by_Mongo(string fault)
    {
        await using var host = await setup.StartAsync();
        var operation = fault == "completed-restore-without-recorded-target"
            ? BackupOperation.RequestRestore(
                Id(),
                TestPlatformDefaults.TenantId,
                Id(),
                "policy",
                "archive",
                new BackupCopySelection("copy"),
                new(Id(), "restored", null),
                Now,
                Origin)
            : Copy();
        var document = operation.ToBsonDocument();
        var execution = document["Work"]["Execution"].AsBsonDocument;
        switch (fault)
        {
            case "completed-copy-without-id":
                document["Work"]["Execution"] = new BsonDocument(
                    "_t",
                    new BsonArray { "BackupCopyExecution", "CompletedBackupCopy" });
                break;
            case "completed-restore-without-recorded-target":
                document["Work"]["Execution"] = new BsonDocument
                {
                    ["_t"] = new BsonArray { "BackupRestoreExecution", "CompletedBackupRestore" },
                    ["Target"] = new BsonDocument(
                        "_t",
                        new BsonArray { "BackupRestoreTarget", "AwaitingRestoreTarget" })
                };
                break;
            case "available-claim-with-lease":
                execution["Dispatch"]["Lease"] = new BsonDocument { ["_id"] = "lease", ["ExpiresAtUtc"] = Now };
                break;
        }

        await RejectedAsync(mongo.Database.GetCollection<BsonDocument>("BackupOperations"), document);
    }

    [Fact]
    public async Task PITR_ticks_round_trip_through_the_validated_collection_and_a_lossy_date_is_rejected()
    {
        await using var host = await setup.StartAsync();
        var time = Now.AddTicks(1_234_560);
        var operation = BackupOperation.RequestRestore(
            Id(),
            TestPlatformDefaults.TenantId,
            "precise",
            "policy",
            "archive",
            new BackupTimeSelection(time),
            new(Id(), "precise", 10),
            Now,
            Origin);
        await PersistAsync("BackupOperations", operation);
        var collection = mongo.Database.GetCollection<BsonDocument>("BackupOperations");
        var raw = await collection.Find(new BsonDocument("_id", operation.Id)).SingleAsync();
        var encoded = raw["Work"]["Selection"]["TargetTimeUtc"].AsBsonDocument;
        Assert.Equal(time.Ticks, encoded["Ticks"].AsInt64);
        Assert.IsType<BsonDateTime>(encoded["DateTime"]);
        var restored = BsonSerializer.Deserialize<BackupOperation>(raw);
        Assert.Equal(
            time,
            Assert.IsType<BackupTimeSelection>(((RestoreBackupWork)restored.Work).Selection).TargetTimeUtc);
        raw["_id"] = Id();
        raw["IdempotencyKey"] = "lossy";
        raw["Work"]["Selection"]["TargetTimeUtc"] = new BsonDateTime(time);
        await RejectedAsync(collection, raw);
    }

    [Fact]
    public async Task Migrations_create_the_real_tenant_history_and_bounded_dispatch_indexes()
    {
        await using var host = await setup.StartAsync();
        using var operations = await mongo.Database.GetCollection<BsonDocument>("BackupOperations").Indexes.ListAsync();
        var operationIndexes = await operations.ToListAsync();
        var key = Assert.Single(operationIndexes, index => index["name"] == "UX_BackupOperations_Tenant_Key");
        Assert.True(key["unique"].AsBoolean);
        Assert.Equal(new BsonDocument { ["TenantId"] = 1, ["IdempotencyKey"] = 1 }, key["key"]);
        Assert.Equal(
            new BsonDocument { ["TenantId"] = 1, ["CreatedAt"] = -1, ["_id"] = -1 },
            Assert.Single(operationIndexes, index => index["name"] == "IX_BackupOperations_Tenant_Created")["key"]);
        Assert.Equal(
            new BsonDocument { ["TenantId"] = 1, ["ArchiveId"] = 1, ["Work.Execution._t"] = 1, ["Work.ScanId"] = 1 },
            Assert.Single(operationIndexes, index => index["name"] == "IX_BackupOperations_ArchiveExecution")["key"]);
        using var archives = await mongo.Database.GetCollection<BsonDocument>("BackupArchives").Indexes.ListAsync();
        Assert.Equal(
            new BsonDocument { ["History.Dispatch.NextAttemptAtUtc"] = 1, ["History.Dispatch.Lease.ExpiresAtUtc"] = 1 },
            Assert.Single(
                await archives.ToListAsync(),
                index => index["name"] == "IX_BackupArchives_HistoryDispatch")["key"]);
    }

    private Task PersistAsync<T>(string name, T document)
        where T : IBackupDocument => mongo.Database
        .GetCollection<T>(name)
        .ReplaceOneAsync(
            Builders<T>.Filter.Eq(value => value.Id, document.Id),
            document,
            new ReplaceOptions { IsUpsert = true });

    private static async Task RejectedAsync(IMongoCollection<BsonDocument> collection, BsonDocument document)
    {
        var failure = await Assert.ThrowsAsync<MongoWriteException>(() => collection.InsertOneAsync(document));
        Assert.Equal(121, failure.WriteError.Code);
    }

    private static BackupDestination Destination() => BackupDestination.Create(
        Id(),
        TestPlatformDefaults.TenantId,
        "storage",
        Id(),
        Id(),
        Storage());

    private static BackupStorage Storage() => new("https://s3.example", "bucket", "", Id(), "id", "key");

    private static BackupArchive Archive() => BackupArchive.Create(
        Id(),
        TestPlatformDefaults.TenantId,
        "policy",
        "destination",
        new("pole/test-backup-database", "source", "Source", 1, "17", 10, 1),
        Storage(),
        new(Id(), Id(), "target", Id(), "namespace", Id(), Id(), Id(), 1, Id(), 1),
        new("scan", Origin),
        Now);

    private static BackupPolicy Policy() => BackupPolicyFactory.Create(
        Id(),
        TestPlatformDefaults.TenantId,
        "daily",
        "pole/test-backup-database",
        "source",
        "destination",
        "archive",
        "0 2 * * *",
        false,
        30,
        Origin,
        Now);

    private static BackupOperation Copy() => BackupOperation.RequestCopy(
        Id(),
        TestPlatformDefaults.TenantId,
        Id(),
        "policy",
        "archive",
        Now,
        Origin);

    private static string Id() => ObjectId.GenerateNewId().ToString();
}
