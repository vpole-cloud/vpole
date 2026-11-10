using MongoDB.Driver;

namespace Pole.Tests.Integration.Infrastructure;

public sealed class MongoDocumentContractTester<TDocument>(
    IMongoDatabase database,
    string collectionName
) : Testing.MongoDocumentContractTester<TDocument>(database, collectionName)
    where TDocument : notnull;
