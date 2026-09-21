using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Xunit;

namespace Pole.Tests.Skrynia;

public sealed class OutcomeTransactionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Outcome_controls_commit_and_abort_without_changing_Pole_callback(bool abort)
    {
        using var fixture = new SkryniaFixture();
        var documents = fixture.Database.GetCollection<BsonDocument>("outcomes");
        var runner = new MongoRunTransaction(fixture.Client);
        var outcome = await runner.InvokeOutcomeAsync<string>(async (transaction, token) =>
        {
            await documents.InsertOneAsync(transaction.Session, new BsonDocument("_id", 1), cancellationToken: token);
            Assert.Equal(1, await documents.CountDocumentsAsync(transaction.Session, FilterDefinition<BsonDocument>.Empty));
            Assert.Equal(0, await documents.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
            return abort ? transaction.Abort("rejected") : transaction.Commit("created");
        });
        Assert.Equal(abort ? "rejected" : "created", outcome);
        Assert.Equal(abort ? 0 : 1, await documents.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        fixture.Reopen();
        Assert.Equal(abort ? 0 : 1, fixture.Database.GetCollection<BsonDocument>("outcomes")
            .CountDocuments(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task Callback_exception_is_preserved_and_all_writes_are_rolled_back()
    {
        using var fixture = new SkryniaFixture();
        var documents = fixture.Database.GetCollection<BsonDocument>("exceptions");
        var expected = new InvalidOperationException("expected failure");
        var runner = new MongoRunTransaction(fixture.Client);
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.InvokeOutcomeAsync<string>(async (transaction, token) =>
        {
            await documents.InsertOneAsync(transaction.Session, new BsonDocument("_id", 1), cancellationToken: token);
            throw expected;
        }));
        Assert.Same(expected, actual);
        Assert.Equal(0, await documents.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        await documents.InsertOneAsync(new BsonDocument("_id", 2));
    }

    [Fact]
    public async Task Cancellation_rolls_back_and_releases_the_writer()
    {
        using var fixture = new SkryniaFixture();
        using var cancellation = new CancellationTokenSource();
        var documents = fixture.Database.GetCollection<BsonDocument>("cancellation");
        var runner = new MongoRunTransaction(fixture.Client);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.InvokeOutcomeAsync<string>(async (transaction, token) =>
        {
            await documents.InsertOneAsync(transaction.Session, new BsonDocument("_id", 1), cancellationToken: token);
            await cancellation.CancelAsync();
            token.ThrowIfCancellationRequested();
            return transaction.Commit("unreachable");
        }, cancellation.Token));
        Assert.Equal(0, await documents.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        await documents.InsertOneAsync(new BsonDocument("_id", 2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Duplicate_key_keeps_driver_exception_and_rolls_back_prior_write(bool secondary)
    {
        using var fixture = new SkryniaFixture();
        var documents = fixture.Database.GetCollection<BsonDocument>("duplicates");
        if (secondary) await documents.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument("key", 1), new CreateIndexOptions { Unique = true }));
        await documents.InsertOneAsync(new BsonDocument { ["_id"] = 1, ["key"] = "taken" });
        var runner = new MongoRunTransaction(fixture.Client);
        var error = await Assert.ThrowsAsync<MongoWriteException>(() => runner.InvokeOutcomeAsync<string>(async (transaction, token) =>
        {
            await documents.InsertOneAsync(transaction.Session, new BsonDocument { ["_id"] = 2, ["key"] = "new" }, cancellationToken: token);
            await documents.InsertOneAsync(transaction.Session, new BsonDocument { ["_id"] = secondary ? 3 : 1, ["key"] = "taken" }, cancellationToken: token);
            return transaction.Commit("unreachable");
        }));
        Assert.Equal(ServerErrorCategory.DuplicateKey, error.WriteError.Category);
        Assert.Equal(11000, error.WriteError.Code);
        Assert.Equal(1, await documents.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }
}
