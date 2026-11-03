using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Pole.Database;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Database;

[Collection(IntegrationTestCollection.Name)]
public sealed class MongoServerTimeFilterTests(MongoReplicaSetFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Renamed_nested_dates_are_compared_against_the_database_clock()
    {
        var documents = fixture.Database.GetCollection<MappedDocument>("ServerTimeFilterTests");
        var cases = new[] { (Id: "past", Offset: -86_400_000L), (Id: "server-now", Offset: 0L),
            (Id: "future", Offset: 86_400_000L) };
        await documents.InsertManyAsync(cases.Select(item =>
            new MappedDocument(item.Id, new(DateTime.UnixEpoch))));
        foreach (var item in cases)
            await documents.UpdateOneAsync(document => document.Id == item.Id,
                new PipelineUpdateDefinition<MappedDocument>(new[]
                {
                    new BsonDocument("$set", new BsonDocument("mapped_envelope.mapped_deadline",
                        new BsonDocument("$add", new BsonArray { "$$NOW", item.Offset })))
                }));

        var after = Builders<MappedDocument>.Filter.AfterServerNow(document => document.Envelope.Deadline);
        var atOrBefore = Builders<MappedDocument>.Filter.AtOrBeforeServerNow(
            document => document.Envelope.Deadline);
        var future = await documents.Find(after).ToListAsync();
        var expired = await documents.Find(atOrBefore).SortBy(document => document.Id).ToListAsync();

        Assert.Equal("future", Assert.Single(future).Id);
        Assert.Equal(["past", "server-now"], expired.Select(document => document.Id));
        var registry = BsonSerializer.SerializerRegistry;
        var args = new RenderArgs<MappedDocument>(documents.DocumentSerializer, registry);
        Assert.Equal("$$NOW", after.Render(args)["$expr"]["$gt"][1].AsString);
        Assert.Equal("$$NOW", atOrBefore.Render(args)["$expr"]["$lte"][1].AsString);
    }

    private sealed record MappedDocument(
        [property: BsonId] string Id,
        [property: BsonElement("mapped_envelope")] MappedEnvelope Envelope);

    private sealed record MappedEnvelope(
        [property: BsonElement("mapped_deadline")] DateTime Deadline);
}
