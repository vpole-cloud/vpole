using MongoDB.Driver;
using Pole.ResultType;

namespace Pole.Database;

public static class MongoCollectionResultExtensions
{
    public static async Task<Result<TDocument>> InsertOneAsResultAsync<TDocument>(
        this IMongoCollection<TDocument> collection,
        TDocument document,
        Func<Failure> duplicateKeyFailure,
        CancellationToken cancellationToken = default)
        where TDocument : notnull
    {
        try
        {
            await collection.InsertOneAsync(
                document,
                cancellationToken: cancellationToken);
            return document;
        }
        catch (MongoWriteException exception) when (
            exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return duplicateKeyFailure();
        }
    }
}
