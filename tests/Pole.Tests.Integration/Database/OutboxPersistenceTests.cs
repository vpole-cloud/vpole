using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Application.Migrations;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Events;
using Pole.Features.Shared.Models;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Database;

[Collection(IntegrationTestCollection.Name)]
public sealed class OutboxPersistenceTests(MongoReplicaSetFixture fixture)
    : IAsyncLifetime
{
    private const int ConcurrentWriterCount = 12;

    public async Task InitializeAsync()
    {
        await fixture.ResetDatabaseAsync();
        await new OutboxMessageMigration().Up(fixture.Database);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task KeyedWrite_AlreadyPending_IsSuccessfulNoOp()
    {
        var runtimeCellId = ObjectId.GenerateNewId().ToString();
        var pendingKey = RuntimeCellPendingKey.Reconciliation(runtimeCellId, 1);

        await EnqueueAsync(runtimeCellId, pendingKey);
        await EnqueueAsync(runtimeCellId, pendingKey);

        var messages = await fixture.DbContext.OutboxMessages
            .Find(message => message.PendingKey == pendingKey)
            .ToListAsync();
        var message = Assert.Single(messages);
        Assert.IsType<PendingOutboxMessageState>(message.State);
    }

    [Fact]
    public async Task KeyedWrite_ConcurrentTransactions_CreateOnePendingMessage()
    {
        var runtimeCellId = ObjectId.GenerateNewId().ToString();
        var pendingKey = RuntimeCellPendingKey.Reconciliation(runtimeCellId, 1);
        var callbacksReady = 0;
        var releaseCallbacks = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var writes = Enumerable.Range(0, ConcurrentWriterCount)
            .Select(_writerIndex => fixture.DbContext.RunInTransactionAsync(
                async (session, cancellationToken) =>
                {
                    _ = await fixture.DbContext.OutboxMessages
                        .Find(session, Builders<OutboxMessage>.Filter.Empty)
                        .Limit(1)
                        .FirstOrDefaultAsync(cancellationToken);
                    if (Interlocked.Increment(ref callbacksReady) ==
                        ConcurrentWriterCount)
                    {
                        releaseCallbacks.TrySetResult();
                    }

                    await releaseCallbacks.Task.WaitAsync(cancellationToken);
                    await fixture.DbContext.StoreEventAsync(
                        CreateWrite(runtimeCellId, pendingKey),
                        session,
                        cancellationToken);
                    return Result.Success();
                }))
            .ToArray();

        var results = await Task.WhenAll(writes);

        foreach (var result in results)
            result.AssertSuccess();
        Assert.Equal(
            1L,
            await fixture.DbContext.OutboxMessages.CountDocumentsAsync(
                message => message.PendingKey == pendingKey));
    }

    [Fact]
    public async Task TerminalTransition_ReleasesPendingKey()
    {
        var runtimeCellId = ObjectId.GenerateNewId().ToString();
        var pendingKey = RuntimeCellPendingKey.Reconciliation(runtimeCellId, 1);
        await EnqueueAsync(runtimeCellId, pendingKey);

        var pending = await fixture.DbContext.OutboxMessages
            .Find(message => message.PendingKey == pendingKey)
            .SingleAsync();
        await fixture.DbContext.OutboxMessages.ReplaceOneAsync(
            message => message.Id == pending.Id,
            pending.MarkPublished(DateTime.UtcNow.AddSeconds(1)));

        await EnqueueAsync(runtimeCellId, pendingKey);

        Assert.Equal(
            2L,
            await fixture.DbContext.OutboxMessages.CountDocumentsAsync(
                Builders<OutboxMessage>.Filter.Empty));
        Assert.Equal(
            1L,
            await fixture.DbContext.OutboxMessages.CountDocumentsAsync(
                message => message.PendingKey == pendingKey));
    }

    [Fact]
    public async Task Migration_CreatesDeliveryAndDeduplicationIndexes()
    {
        await new OutboxMessageMigration().Up(fixture.Database);

        var indexes = await (await fixture.DbContext.OutboxMessages.Indexes.ListAsync())
            .ToListAsync();

        Assert.Contains(indexes, index =>
            index["name"] == OutboxMessageMigration.PendingDueIndexName);
        Assert.Contains(indexes, index =>
            index["name"] == OutboxMessageMigration.PendingKeyIndexName &&
            index["unique"].AsBoolean &&
            index["partialFilterExpression"]["PendingKey"]["$type"] == "string");
        Assert.Contains(indexes, index =>
            index["name"] == OutboxMessageMigration.PublishedCleanupIndexName);
        Assert.Contains(indexes, index =>
            index["name"] == OutboxMessageMigration.AbandonedCleanupIndexName);
    }

    private async Task EnqueueAsync(
        string runtimeCellId,
        string pendingKey)
    {
        var result = await fixture.DbContext.RunInTransactionAsync(
            async (session, cancellationToken) =>
            {
                await fixture.DbContext.StoreEventAsync(
                    CreateWrite(runtimeCellId, pendingKey),
                    session,
                    cancellationToken);
                return Result.Success();
            });
        result.AssertSuccess();
    }

    private static OutboxWrite CreateWrite(
        string runtimeCellId,
        string pendingKey) =>
        OutboxWrite.Create(
            new RuntimeCellReconciliationRequestedEvent(runtimeCellId, 1)
            {
                EventCore = EventCore.Create(TestData.TenantId)
            },
            pendingKey);
}
