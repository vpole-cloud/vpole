using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Pole.Testing;

public class MongoDocumentContractTester<TDocument>(
    IMongoDatabase database,
    string collectionName
)
    where TDocument : notnull
{
    private readonly IMongoCollection<BsonDocument> _documents = database.GetCollection<BsonDocument>(collectionName);

    public async Task AssertAcceptsAsync(
        IReadOnlyCollection<TDocument> documents,
        CancellationToken cancellationToken = default)
    {
        var serializedDocuments = documents.Select(document => document.ToBsonDocument()).ToList();

        await _documents.InsertManyAsync(serializedDocuments, cancellationToken: cancellationToken);
    }

    public async Task AssertRejectsAsync(
        TDocument document,
        Action<BsonDocument> makeInvalid,
        CancellationToken cancellationToken = default)
    {
        var serializedDocument = document.ToBsonDocument();
        makeInvalid(serializedDocument);

        var exception = await Assert.ThrowsAsync<MongoWriteException>(() =>
            _documents.InsertOneAsync(serializedDocument, cancellationToken: cancellationToken));

        Assert.Equal(121, exception.WriteError.Code);
    }

    public async Task AssertStrictErrorValidationAsync(CancellationToken cancellationToken = default)
    {
        using var collections = await database.ListCollectionsAsync(
            new ListCollectionsOptions
            {
                Filter = new BsonDocument("name", collectionName),
            },
            cancellationToken);
        var collection = Assert.Single(await collections.ToListAsync(cancellationToken));
        var options = collection["options"].AsBsonDocument;

        Assert.Equal("strict", options["validationLevel"].AsString);
        Assert.Equal("error", options["validationAction"].AsString);
        Assert.True(options.Contains("validator"));
    }
}
