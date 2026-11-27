// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Database;

[Collection(IntegrationTestCollection.Global2)]
public sealed class MongoDuplicateKeyPersistenceTests(
    MongoReplicaSetFixture fixture
) : MongoIntegrationTestBase(fixture)
{
    private const string NameIndex = "UX_Classifier_Name";

    private IMongoCollection<BsonDocument> Documents =>
        Fixture.Database.GetCollection<BsonDocument>("DuplicateKeyClassifierTests");

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await Documents.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("Name"),
                new CreateIndexOptions
                {
                    Unique = true,
                    Name = NameIndex, Collation = new Collation("en", strength: CollationStrength.Secondary),
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("Other"),
                new CreateIndexOptions
                {
                    Unique = true,
                    Name = "UX_Classifier_Other",
                }),
        ]);
        await Documents.InsertOneAsync(Document("original", "name", "other"));
    }

    public override Task DisposeAsync() => base.DisposeAsync();

    [Fact]
    public async Task Driver_write_errors_distinguish_name_other_and_identity_indexes()
    {
        var name = await Assert.ThrowsAsync<MongoWriteException>(() =>
            Documents.InsertOneAsync(Document("new", "name", "different")));
        Assert.True(MongoDuplicateKey.IsForIndex(name, NameIndex));
        var other = await Assert.ThrowsAsync<MongoWriteException>(() =>
            Documents.InsertOneAsync(Document("new", "different", "other")));
        Assert.False(MongoDuplicateKey.IsForIndex(other, NameIndex));
        var identity = await Assert.ThrowsAsync<MongoWriteException>(() =>
            Documents.InsertOneAsync(Document("original", "different", "different")));
        Assert.False(MongoDuplicateKey.IsForIndex(identity, NameIndex));
    }

    [Fact]
    public async Task Bulk_error_is_classified_only_when_every_failure_is_the_expected_index()
    {
        var expected = await Assert.ThrowsAsync<MongoBulkWriteException<BsonDocument>>(() => Documents.InsertManyAsync(
            [Document("one", "name", "one")],
            new InsertManyOptions { IsOrdered = false }));
        Assert.True(MongoDuplicateKey.IsForIndex(expected, NameIndex));
        var mixed = await Assert.ThrowsAsync<MongoBulkWriteException<BsonDocument>>(() => Documents.InsertManyAsync(
            [Document("two", "name", "two"), Document("three", "three", "other")],
            new InsertManyOptions { IsOrdered = false }));
        Assert.False(MongoDuplicateKey.IsForIndex(mixed, NameIndex));
    }

    private static BsonDocument Document(
        string id,
        string name,
        string other) => new()
    {
        ["_id"] = id,
        ["Name"] = name,
        ["Other"] = other,
    };
}
