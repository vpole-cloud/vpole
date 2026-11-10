using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Migrations;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Events;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Database;

[Collection(IntegrationTestCollection.Global2)]
public sealed class OutboxPersistenceTests(
    MongoReplicaSetFixture fixture
) : MongoIntegrationTestBase(fixture)
{
    private const int ConcurrentWriterCount = 12;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await new OutboxMessageMigration().Up(Fixture.Database);
    }

    public override Task DisposeAsync() => base.DisposeAsync();

    [Fact]
    public async Task KeyedWrite_AlreadyPending_IsSuccessfulNoOp()
    {
        var runtimeCellId = ObjectId.GenerateNewId().ToString();
        var pendingKey = RuntimeCellPendingKey.Reconciliation(runtimeCellId, 1);

        await EnqueueAsync(runtimeCellId, pendingKey);
        await EnqueueAsync(runtimeCellId, pendingKey);

        var messages = await Fixture.DbContext
            .OutboxMessages
            .Find(message => message.PendingKey == pendingKey)
            .ToListAsync();
        var message = Assert.Single(messages);
        Assert.IsType<PendingOutboxMessageState>(message.State);
    }

    [Fact]
    public async Task OutcomeTransaction_AbortRollsBackMutationAndOutboxTogether()
    {
        var documents = Fixture.Database.GetCollection<BsonDocument>("transaction-outcome-outbox-tests");
        var runtimeCellId = ObjectId.GenerateNewId().ToString();

        var outcome = await Fixture.DbContext.RunOutcomeInTransactionAsync<string>(async (
            transaction, cancellationToken) =>
        {
            await documents.InsertOneAsync(
                transaction.Session,
                new BsonDocument("name", "must-not-commit"),
                cancellationToken: cancellationToken);
            await Fixture.StoreOutboxWrites(
                transaction.Session,
                [CreateWrite(runtimeCellId, pendingKey: null)],
                cancellationToken);

            return transaction.Abort("rejected");
        });

        Assert.Equal("rejected", outcome);
        Assert.Equal(0L, await documents.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(
            0L,
            await Fixture.DbContext.OutboxMessages.CountDocumentsAsync(Builders<OutboxMessage>.Filter.Empty));
    }

    [Fact]
    public async Task KeyedWrite_ConcurrentTransactions_CreateOnePendingMessage()
    {
        var runtimeCellId = ObjectId.GenerateNewId().ToString();
        var pendingKey = RuntimeCellPendingKey.Reconciliation(runtimeCellId, 1);
        var callbacksReady = 0;
        var releaseCallbacks = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var writes = Enumerable.Range(0, ConcurrentWriterCount)
            .Select(_writerIndex =>
                Fixture.DbContext.RunOutcomeInTransactionAsync<Unit>(async (transaction, cancellationToken) =>
                {
                    _ = await Fixture.DbContext
                        .OutboxMessages
                        .Find(transaction.Session, Builders<OutboxMessage>.Filter.Empty)
                        .Limit(1)
                        .FirstOrDefaultAsync(cancellationToken);
                    if (Interlocked.Increment(ref callbacksReady) == ConcurrentWriterCount)
                        releaseCallbacks.TrySetResult();

                    await releaseCallbacks.Task.WaitAsync(cancellationToken);
                    await Fixture.StoreOutboxWrites(
                        transaction.Session,
                        [CreateWrite(runtimeCellId, pendingKey)],
                        cancellationToken);
                    return transaction.Commit(Unit.Value);
                }))
            .ToArray();

        var results = await Task.WhenAll(writes);

        Assert.All(results, result => Assert.Equal(Unit.Value, result));
        Assert.Equal(
            1L,
            await Fixture.DbContext.OutboxMessages.CountDocumentsAsync(message => message.PendingKey == pendingKey));
    }

    [Fact]
    public async Task TerminalTransition_ReleasesPendingKey()
    {
        var runtimeCellId = ObjectId.GenerateNewId().ToString();
        var pendingKey = RuntimeCellPendingKey.Reconciliation(runtimeCellId, 1);
        await EnqueueAsync(runtimeCellId, pendingKey);

        var pending = await Fixture.DbContext
            .OutboxMessages
            .Find(message => message.PendingKey == pendingKey)
            .SingleAsync();
        await Fixture.DbContext.OutboxMessages.ReplaceOneAsync(
            message => message.Id == pending.Id,
            pending.MarkPublished(DateTime.UtcNow.AddSeconds(1)));

        await EnqueueAsync(runtimeCellId, pendingKey);

        Assert.Equal(
            2L,
            await Fixture.DbContext.OutboxMessages.CountDocumentsAsync(Builders<OutboxMessage>.Filter.Empty));
        Assert.Equal(
            1L,
            await Fixture.DbContext.OutboxMessages.CountDocumentsAsync(message => message.PendingKey == pendingKey));
    }

    [Fact]
    public async Task Migration_CreatesDeliveryAndDeduplicationIndexes()
    {
        await new OutboxMessageMigration().Up(Fixture.Database);

        var indexes = await (await Fixture.DbContext.OutboxMessages.Indexes.ListAsync()).ToListAsync();

        Assert.Contains(indexes, index => index["name"] == OutboxMessageMigration.PendingDueIndexName);
        Assert.Contains(
            indexes,
            index => index["name"] == OutboxMessageMigration.PendingKeyIndexName && index["unique"].AsBoolean &&
                index["partialFilterExpression"]["PendingKey"]["$type"] == "string");
        Assert.Contains(indexes, index => index["name"] == OutboxMessageMigration.PublishedCleanupIndexName);
        Assert.Contains(indexes, index => index["name"] == OutboxMessageMigration.AbandonedCleanupIndexName);
    }

    private async Task EnqueueAsync(string runtimeCellId, string pendingKey)
    {
        var result
            = await Fixture.DbContext.RunOutcomeInTransactionAsync<Unit>(async (transaction, cancellationToken) =>
            {
                await Fixture.StoreOutboxWrites(
                    transaction.Session,
                    [CreateWrite(runtimeCellId, pendingKey)],
                    cancellationToken);
                return transaction.Commit(Unit.Value);
            });
        Assert.Equal(Unit.Value, result);
    }

    private OutboxWrite CreateWrite(string runtimeCellId, string? pendingKey) => OutboxWrite.Create(
        new RuntimeCellReconciliationRequestedEvent(runtimeCellId, 1)
        {
            EventCore = EventCore.Create(Data.TenantId),
        },
        pendingKey);
}
