using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Pole.Database;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Database;

[Collection(IntegrationTestCollection.Name)]
public sealed class MongoCollectionResultExtensionsTests(MongoReplicaSetFixture fixture)
    : IAsyncLifetime
{
    private IMongoCollection<InsertSubject> Collection =>
        fixture.Database.GetCollection<InsertSubject>("MongoInsertResultSubjects");

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task InsertOneAsResult_ReturnsInsertedDocument()
    {
        var subject = new InsertSubject(ObjectId.GenerateNewId().ToString(), "value");

        var result = await Collection.InsertOneAsResultAsync(
            subject,
            () => Failures.Conflict("duplicate"));

        Assert.True(result.TryGetValue(out var inserted, out _));
        Assert.Equal(subject, inserted);
        Assert.Equal(subject, await Collection.Find(item => item.Id == subject.Id).SingleAsync());
    }

    [Fact]
    public async Task InsertOneAsResult_UsesCallerFailureForDuplicateKey()
    {
        var subject = new InsertSubject(ObjectId.GenerateNewId().ToString(), "first");
        await Collection.InsertOneAsync(subject);

        var result = await Collection.InsertOneAsResultAsync(
            subject with { Value = "second" },
            () => Failures.Conflict("Subject already exists."));

        Assert.False(result.TryGetValue(out _, out var failure));
        var conflict = Assert.IsType<ConflictFailure>(failure);
        Assert.Equal("Subject already exists.", conflict.Message);
    }

    private sealed record InsertSubject(
        [property: BsonId]
        [property: BsonRepresentation(BsonType.ObjectId)]
        string Id,
        string Value);
}
