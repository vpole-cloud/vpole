using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Database;

public sealed class ClientSessionHandleExtensionsTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task RunInTransactionAsync_AbortsWrites_WhenCallbackReturnsFailure()
    {
        var documents = Fixture.Database.GetCollection<BsonDocument>("transaction-result-tests");

        var result = await Fixture.DbContext.RunInTransactionAsync(async (session, cancellationToken) =>
        {
            await documents.InsertOneAsync(
                session,
                new BsonDocument("name", "must-not-commit"),
                cancellationToken: cancellationToken);

            return (Result)Failures.Conflict("Expected command failure.");
        });

        Assert.IsType<ConflictFailure>(result);
        Assert.Equal(0L, await documents.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task GenericRunInTransactionAsync_AbortsWrites_WhenCallbackReturnsFailure()
    {
        var documents = Fixture.Database.GetCollection<BsonDocument>("generic-transaction-result-tests");

        var result = await Fixture.DbContext.RunInTransactionAsync<string>(async (session, cancellationToken) =>
        {
            await documents.InsertOneAsync(
                session,
                new BsonDocument("name", "must-not-commit"),
                cancellationToken: cancellationToken);

            return Failures.Conflict("Expected command failure.");
        });

        Assert.False(result.TryGetValue(out _, out var failure));
        Assert.IsType<ConflictFailure>(failure);
        Assert.Equal(0L, await documents.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }
}
