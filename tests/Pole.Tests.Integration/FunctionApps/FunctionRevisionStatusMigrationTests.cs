using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Functions.Migrations;
using Pole.Features.Functions.Domain;
using Pole.Functions.Protocol;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.FunctionApps;

[Collection(IntegrationTestCollection.Name)]
public sealed class FunctionRevisionStatusMigrationTests(MongoReplicaSetFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Legacy_enum_documents_and_indexes_migrate_to_polymorphic_statuses()
    {
        var readyAtUtc = DateTimeOffset.Parse("2026-07-17T10:00:10Z");
        var provisioning = CreateRevision();
        var active = provisioning.MarkReady(readyAtUtc).Activate();
        var failed = (provisioning with { Id = ObjectId.GenerateNewId().ToString() })
            .Fail(new FunctionRevisionFailure("startup", "failed"));
        var revisions = fixture.Database.GetCollection<BsonDocument>("FunctionRevisions");
        await revisions.InsertManyAsync(
        [
            ToLegacy(active, state: 2),
            ToLegacy(failed, state: 4)
        ]);
        await revisions.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("FunctionAppId"),
                new CreateIndexOptions<BsonDocument>
                {
                    Name = "UX_FunctionRevisions_Tenant_App_Active",
                    Unique = true,
                    PartialFilterExpression = Builders<BsonDocument>.Filter.Eq("State", 2)
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("State")
                    .Ascending("TenantId")
                    .Ascending("FunctionAppId"),
                new CreateIndexOptions { Name = "IX_FunctionRevisions_State_Tenant_App" })
        ]);

        await new FunctionRevisionStatusMigration().Up(fixture.Database);

        var typed = await fixture.Database.GetCollection<FunctionRevision>("FunctionRevisions")
            .Find(Builders<FunctionRevision>.Filter.Empty)
            .ToListAsync();
        Assert.Equal(readyAtUtc, Assert.IsType<ActiveFunctionRevisionStatus>(
            typed.Single(revision => revision.Id == active.Id).Status).ReadyAtUtc);
        Assert.Equal("startup", Assert.IsType<FailedFunctionRevisionStatus>(
            typed.Single(revision => revision.Id == failed.Id).Status).Failure.Code);
        var migratedDocuments = await revisions.Find(Builders<BsonDocument>.Filter.Empty).ToListAsync();
        Assert.All(migratedDocuments, document =>
        {
            Assert.True(document.Contains("Status"));
            Assert.False(document.Contains("State"));
            Assert.False(document.Contains("ReadyAtUtc"));
            Assert.False(document.Contains("Failure"));
        });

        var indexes = await (await revisions.Indexes.ListAsync()).ToListAsync();
        var activeIndex = indexes.Single(index =>
            index["name"] == "UX_FunctionRevisions_Tenant_App_Active");
        Assert.Equal("active", activeIndex["partialFilterExpression"]["Status._t"].AsString);
        var lifecycleIndex = indexes.Single(index =>
            index["name"] == "IX_FunctionRevisions_Status_Tenant_App");
        Assert.True(lifecycleIndex["key"].AsBsonDocument.Contains("Status._t"));
        Assert.False(lifecycleIndex["key"].AsBsonDocument.Contains("State"));

        var duplicateActive = active with
        {
            Id = ObjectId.GenerateNewId().ToString(),
            RevisionNumber = active.RevisionNumber + 1
        };
        await Assert.ThrowsAsync<MongoWriteException>(() =>
            fixture.Database.GetCollection<FunctionRevision>("FunctionRevisions")
                .InsertOneAsync(duplicateActive));
    }

    private static BsonDocument ToLegacy(FunctionRevision revision, int state)
    {
        var document = revision.ToBsonDocument();
        var status = document[nameof(FunctionRevision.Status)].AsBsonDocument;
        document.Remove(nameof(FunctionRevision.Status));
        document["State"] = state;
        document["ReadyAtUtc"] = status.TryGetValue("ReadyAtUtc", out var readyAtUtc)
            ? readyAtUtc.DeepClone()
            : BsonNull.Value;
        document["Failure"] = status.TryGetValue("Failure", out var failure)
            ? failure.DeepClone()
            : BsonNull.Value;
        return document;
    }

    private static FunctionRevision CreateRevision()
    {
        var created = FunctionAppFactory.Create(
            "tenant-1",
            "group-1",
            new FunctionPlacementIdentity(
                "507f1f77bcf86cd799439091",
                "507f1f77bcf86cd799439092",
                "507f1f77bcf86cd799439093"),
            "orders",
            "orders",
            new FunctionRevisionSpecInput(
                new FunctionImage("registry.example", "team/functions", "v1",
                    Digest: null,
                    PullSecretName: null),
                PoleFunctionProtocol.Version,
                new FunctionConfiguration(
                    ImmutableDictionary<string, string>.Empty,
                    ImmutableDictionary<string, FunctionSecretReference>.Empty),
                new FunctionResources(
                    new FunctionResourceValues(100, 128),
                    new FunctionResourceValues(500, 512)),
                new FunctionScalingPolicy(0, 5, 4, 8, 30, 30),
                [
                    new FunctionHandlerSpec(
                        "webhook",
                        4,
                        new HttpFunctionTriggerSpec(
                            string.Empty,
                            "orders",
                            ["POST"],
                            FunctionExposure.Public))
                ]),
            [],
            DateTimeOffset.Parse("2026-07-17T10:00:00Z"));
        return created.Revision;
    }
}
