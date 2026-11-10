using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Database;

[Collection(IntegrationTestCollection.Global)]
public sealed class NativeMongoTypeFilterTests(
    MongoReplicaSetFixture fixture
) : MongoIntegrationTestBase(fixture)
{
    public override Task InitializeAsync() => base.InitializeAsync();
    public override Task DisposeAsync() => base.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_type_filter_matches_nested_renamed_members_with_either_discriminator_shape(
        bool scalarDiscriminator)
    {
        const string collectionName = "NativeTypeFilterTests";
        var raw = Fixture.Database.GetCollection<BsonDocument>(collectionName);
        var documents = new[]
        {
            new MappedDocument("ready", new MappedEnvelope(new ReadyState("available"))).ToBsonDocument(),
            new MappedDocument("waiting", new MappedEnvelope(new WaitingState())).ToBsonDocument(),
        };
        if (scalarDiscriminator)
            foreach (var document in documents)
            {
                var state = document["mapped_envelope"]["mapped_state"].AsBsonDocument;
                state["_t"] = state["_t"].AsBsonArray.Last();
            }

        await raw.InsertManyAsync(documents);

        var typed = Fixture.Database.GetCollection<MappedDocument>(collectionName);
        var filter
            = Builders<MappedDocument>.Filter.OfType<FilterState, ReadyState>(document => document.Envelope.State);
        var matches = await typed.Find(filter).ToListAsync();

        var ready = Assert.Single(matches);
        Assert.Equal("ready", ready.Id);
        Assert.Equal("available", Assert.IsType<ReadyState>(ready.Envelope.State).Detail);
    }

    private sealed record MappedDocument(
        [property: BsonId] string Id,
        [property: BsonElement("mapped_envelope")]
        MappedEnvelope Envelope
    );

    private sealed record MappedEnvelope(
        [property: BsonElement("mapped_state")]
        FilterState State
    );

    [BsonDiscriminator(RootClass = true)]
    [BsonKnownTypes(typeof(ReadyState), typeof(WaitingState))]
    private abstract record FilterState;

    [BsonDiscriminator("ready-state")]
    private sealed record ReadyState(
        string Detail
    ) : FilterState;

    [BsonDiscriminator("waiting-state")]
    private sealed record WaitingState : FilterState;
}
