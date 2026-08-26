using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Database;

public sealed class MongoTestStoreTransactionExtensionsTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task OutcomeTransaction_CommitsWritesAndReturnsOutcome()
    {
        var documents = Fixture.Database.GetCollection<BsonDocument>(
            "transaction-outcome-commit-tests");

        var outcome = await Fixture.DbContext.RunOutcomeInTransactionAsync<string>(
            async (transaction, cancellationToken) =>
            {
                await documents.InsertOneAsync(
                    transaction.Session,
                    new BsonDocument("name", "committed"),
                    cancellationToken: cancellationToken);

                return transaction.Commit("created");
            });

        Assert.Equal("created", outcome);
        Assert.Equal(
            1L,
            await documents.CountDocumentsAsync(
                FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task OutcomeTransaction_AbortsWritesAndReturnsOutcome()
    {
        var documents = Fixture.Database.GetCollection<BsonDocument>(
            "transaction-outcome-abort-tests");

        var outcome = await Fixture.DbContext.RunOutcomeInTransactionAsync<string>(
            async (transaction, cancellationToken) =>
            {
                await documents.InsertOneAsync(
                    transaction.Session,
                    new BsonDocument("name", "must-not-commit"),
                    cancellationToken: cancellationToken);

                return transaction.Abort("conflict");
            });

        Assert.Equal("conflict", outcome);
        Assert.Equal(
            0L,
            await documents.CountDocumentsAsync(
                FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task OutcomeTransaction_PropagatesExceptionAndAbortsWrites()
    {
        var documents = Fixture.Database.GetCollection<BsonDocument>(
            "transaction-outcome-exception-tests");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Fixture.DbContext.RunOutcomeInTransactionAsync<string>(
                async (transaction, cancellationToken) =>
                {
                    await documents.InsertOneAsync(
                        transaction.Session,
                        new BsonDocument("name", "must-not-commit"),
                        cancellationToken: cancellationToken);
                    throw new InvalidOperationException("Expected infrastructure failure.");
                }));

        Assert.Equal("Expected infrastructure failure.", exception.Message);
        Assert.Equal(
            0L,
            await documents.CountDocumentsAsync(
                FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task OutcomeTransaction_CancellationAbortsWrites()
    {
        var documents = Fixture.Database.GetCollection<BsonDocument>(
            "transaction-outcome-cancellation-tests");
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Fixture.DbContext.RunOutcomeInTransactionAsync<string>(
                async (transaction, cancellationToken) =>
                {
                    await documents.InsertOneAsync(
                        transaction.Session,
                        new BsonDocument("name", "must-not-commit"),
                        cancellationToken: cancellationToken);
                    await cancellation.CancelAsync();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

                    return transaction.Commit("unreachable");
                },
                cancellationToken: cancellation.Token));

        Assert.Equal(
            0L,
            await documents.CountDocumentsAsync(
                FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task OutcomeTransaction_DoesNotTranslateDuplicateKeyException()
    {
        var documents = Fixture.Database.GetCollection<BsonDocument>(
            "transaction-outcome-duplicate-tests");
        await documents.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("name"),
                new CreateIndexOptions { Unique = true }));
        await documents.InsertOneAsync(new BsonDocument("name", "existing"));

        var exception = await Assert.ThrowsAsync<MongoWriteException>(() =>
            Fixture.DbContext.RunOutcomeInTransactionAsync<string>(
                async (transaction, cancellationToken) =>
                {
                    await documents.InsertOneAsync(
                        transaction.Session,
                        new BsonDocument("name", "existing"),
                        cancellationToken: cancellationToken);

                    return transaction.Commit("unreachable");
                }));

        Assert.Equal(ServerErrorCategory.DuplicateKey, exception.WriteError?.Category);
        Assert.Equal(
            1L,
            await documents.CountDocumentsAsync(
                FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task OutcomeTransaction_AbortsWrites_WhenCallbackReturnsRejectedOutcome()
    {
        var documents = Fixture.Database.GetCollection<BsonDocument>("transaction-result-tests");

        var result = await Fixture.DbContext.RunOutcomeInTransactionAsync<TransactionRejected>(
            async (transaction, cancellationToken) =>
        {
            await documents.InsertOneAsync(
                transaction.Session,
                new BsonDocument("name", "must-not-commit"),
                cancellationToken: cancellationToken);

            return transaction.Abort(new TransactionRejected("Expected command failure."));
        });

        Assert.Equal("Expected command failure.", result.Message);
        Assert.Equal(0L, await documents.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    private sealed record TransactionRejected(string Message);
}
