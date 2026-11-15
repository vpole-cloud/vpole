using Microsoft.FSharp.Core;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Postgres.Domain;
using Pole.Features.Postgres.Migrations;
using Pole.Migrations;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Postgres;

[Collection(IntegrationTestCollection.Global3)]
public sealed class PostgresManagedDatabaseSchemaValidationTests(
    MongoReplicaSetFixture fixture
) : MongoIntegrationTestBase(fixture)
{
    private const string CollectionName = "PostgresManagedDatabases";
    private static readonly DateTime CreatedAtUtc = new(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AuditedAtUtc = CreatedAtUtc.AddMinutes(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Migration_creates_or_upgrades_strict_collection_and_retries_without_changing_existing_indexes(
        bool existing)
    {
        var documents = Fixture.Database.GetCollection<BsonDocument>(CollectionName);
        if (existing)
        {
            await Fixture.Database.CreateCollectionAsync(CollectionName);
            await documents.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("TenantId")
                        .Ascending("Owner.ResourceType")
                        .Ascending("Owner.ResourceId"),
                    new CreateIndexOptions { Unique = true, Name = "UX_PostgresManagedDatabases_Owner" }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("ClusterId"),
                    new CreateIndexOptions { Name = "IX_PostgresManagedDatabases_Cluster" })
            ]);
        }

        IMigration migration = new PostgresManagedDatabaseIndexesMigration();
        await migration.Up(Fixture.Database);
        var indexesBeforeRetry = await documents.Indexes.List().ToListAsync();
        await migration.Up(Fixture.Database);
        var indexesAfterRetry = await documents.Indexes.List().ToListAsync();
        Assert.Equal(indexesBeforeRetry, indexesAfterRetry);
        Assert.Equal(3, indexesAfterRetry.Count);
        var owner = Assert.Single(indexesAfterRetry, index => index["name"] == "UX_PostgresManagedDatabases_Owner");
        Assert.True(owner["unique"].AsBoolean);
        Assert.Equal(
            new BsonDocument { ["TenantId"] = 1, ["Owner.ResourceType"] = 1, ["Owner.ResourceId"] = 1 },
            owner["key"].AsBsonDocument);
        var cluster = Assert.Single(indexesAfterRetry, index => index["name"] == "IX_PostgresManagedDatabases_Cluster");
        Assert.Equal(new BsonDocument { ["TenantId"] = 1, ["ClusterId"] = 1 }, cluster["key"].AsBsonDocument);
        var contract = Contract();
        await contract.AssertStrictErrorValidationAsync();

        var active = CreateClaim();
        var purging = CreateClaim().BeginPurge(CreatedAtUtc.AddMinutes(1));
        var purged = WithAudit(
            CreateClaim().BeginPurge(CreatedAtUtc.AddMinutes(1)).CompletePurge(CreatedAtUtc.AddMinutes(2)));
        var audited = WithAudit(CreateClaim());
        var typed = Fixture.Database.GetCollection<PostgresManagedDatabase>(CollectionName);
        await typed.InsertManyAsync([active, purging, purged, audited]);
        var stored = await typed.Find(_ => true).ToListAsync();
        Assert.IsType<ActivePostgresManagedDatabase>(Assert.Single(stored, claim => claim.Id == active.Id).State);
        Assert.IsType<PurgingPostgresManagedDatabase>(Assert.Single(stored, claim => claim.Id == purging.Id).State);
        var tombstone = Assert.Single(stored, claim => claim.Id == purged.Id);
        Assert.IsType<PurgedPostgresManagedDatabase>(tombstone.State);
        Assert.Equal(AuditedAtUtc, tombstone.LastAuditAtUtc.Value);
        Assert.Null(Assert.Single(stored, claim => claim.Id == active.Id).LastAuditAtUtc);
        Assert.Equal(AuditedAtUtc, Assert.Single(stored, claim => claim.Id == audited.Id).LastAuditAtUtc.Value);
        var bson = audited.ToBsonDocument();
        Assert.Equal(BsonType.ObjectId, bson["TenantId"].BsonType);
        Assert.Equal(BsonType.ObjectId, bson["ClusterId"].BsonType);
        Assert.Equal(BsonType.ObjectId, bson["Owner"]["ResourceId"].BsonType);
        Assert.Equal(BsonType.String, bson["Source"]["ResourceGroupId"].BsonType);
        Assert.Equal(BsonType.String, bson["Target"]["DeploymentId"].BsonType);
        Assert.Equal(BsonType.Int64, bson["Revision"].BsonType);
        Assert.Equal(BsonType.DateTime, bson["LastAuditAtUtc"]["Value"].BsonType);
    }

    [Fact]
    public async Task
        Strict_validator_rejects_missing_authority_wrong_BSON_types_unknown_states_and_invalid_audit_option()
    {
        await ((IMigration)new PostgresManagedDatabaseIndexesMigration()).Up(Fixture.Database);
        Action<BsonDocument>[] malformed =
        [
            document => document.Remove("TenantId"),
            document => document["TenantId"] = new BsonString(Id()),
            document => document["ClusterId"] = new BsonString(Id()),
            document => document["CredentialId"] = new BsonString(Id()),
            document => document["Owner"]["ResourceId"] = new BsonString(Id()),
            document => document["Owner"]["ResourceType"] = "",
            document => document["Source"].AsBsonDocument.Remove("Component"),
            document => document["Source"]["ResourceGroupId"] = ObjectId.GenerateNewId(),
            document => document["Target"]["DeploymentId"] = ObjectId.GenerateNewId(),
            document => document["Target"].AsBsonDocument.Remove("KubernetesNamespace"),
            document => document["Revision"] = new BsonInt32(1),
            document => document["Revision"] = new BsonInt64(0),
            document => document["CreatedAtUtc"] = "not-a-date",
            document => document["Database"] = new BsonInt32(1),
            document => document["Username"] = "",
            document => document["State"] = new BsonDocument(
                "_t",
                new BsonArray { "PostgresManagedDatabaseState", "Unknown" }),
            document => document["State"] = new BsonDocument(
                "_t",
                new BsonArray { "PostgresManagedDatabaseState", "PurgingPostgresManagedDatabase" }),
            document => document["State"] = new BsonDocument
            {
                ["_t"] = new BsonArray { "PostgresManagedDatabaseState", "PurgedPostgresManagedDatabase" },
                ["CompletedAtUtc"] = "not-a-date"
            },
            document => document["LastAuditAtUtc"] = new BsonDateTime(AuditedAtUtc),
            document => document["LastAuditAtUtc"] = new BsonDocument("Value", "not-a-date"),
            document => document["LastAuditAtUtc"] = new BsonDocument
                { ["Value"] = new BsonDateTime(AuditedAtUtc), ["Unexpected"] = true },
            document => document["Unexpected"] = true,
            document => document["Owner"]["Unexpected"] = true
        ];
        foreach (var corrupt in malformed)
            await Contract().AssertRejectsAsync(CreateClaim(), corrupt);
    }

    [Fact]
    public async Task Missing_audit_field_is_rejected_and_auditing_a_purged_claim_remains_valid()
    {
        await ((IMigration)new PostgresManagedDatabaseIndexesMigration()).Up(Fixture.Database);
        var claim = CreateClaim();
        await Contract().AssertRejectsAsync(claim, document => document.Remove("LastAuditAtUtc"));
        var typed = Fixture.Database.GetCollection<PostgresManagedDatabase>(CollectionName);
        await typed.InsertOneAsync(claim);
        Assert.Null((await typed.Find(item => item.Id == claim.Id).SingleAsync()).LastAuditAtUtc);
        var purged = claim.BeginPurge(CreatedAtUtc.AddMinutes(1)).CompletePurge(CreatedAtUtc.AddMinutes(2));
        await typed.ReplaceOneAsync(item => item.Id == claim.Id, purged);
        await typed.UpdateOneAsync(
            item => item.Id == claim.Id,
            Builders<PostgresManagedDatabase>.Update.Set(
                item => item.LastAuditAtUtc,
                FSharpOption<DateTime>.Some(AuditedAtUtc)));
        var stored = await typed.Find(item => item.Id == claim.Id).SingleAsync();
        Assert.True(stored.IsPurged);
        Assert.Equal(AuditedAtUtc, stored.LastAuditAtUtc.Value);
    }

    private MongoDocumentContractTester<PostgresManagedDatabase> Contract() => new(Fixture.Database, CollectionName);
    private static string Id() => ObjectId.GenerateNewId().ToString();

    private static PostgresManagedDatabase CreateClaim()
    {
        var target = Id();
        return PostgresManagedDatabase.Create(
            Id(),
            Id(),
            new PostgresDatabaseOwner("pole/seaweedfs", Id()),
            new PostgresDatabaseSource(Id(), Id(), target, "filer"),
            new PostgresDatabaseTarget(Id(), target, Id(), "pole-managed-schema"),
            Id(),
            CreatedAtUtc);
    }

    private static PostgresManagedDatabase WithAudit(PostgresManagedDatabase claim) => new(
        claim.Id,
        claim.TenantId,
        claim.ClusterId,
        claim.Owner,
        claim.Source,
        claim.Target,
        claim.CredentialId,
        claim.Database,
        claim.Username,
        claim.State,
        claim.Revision,
        claim.CreatedAtUtc,
        claim.UpdatedAtUtc,
        FSharpOption<DateTime>.Some(AuditedAtUtc));
}
