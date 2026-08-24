using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Application;
using Pole.Features.Outbox.Application.Migrations;
using Pole.Features.Outbox.Application.Persistence;
using Pole.Features.ResourceGroups.Events;
using Pole.Features.Shared.Models;
using Pole.Tests.Integration.Infrastructure;
using Xunit;
using EventExtensions = Pole.Features.Events.Abstractions.EventExtensions;

namespace Pole.Tests.Integration.Database;

[Collection(IntegrationTestCollection.Name)]
public sealed class OutboxProcessorLifecycleTests(
    MongoReplicaSetFixture fixture) : IAsyncLifetime
{
    private static readonly DateTime InitialNowUtc =
        new(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
    private readonly MutableTimeProvider _timeProvider =
        new(new DateTimeOffset(InitialNowUtc));
    private readonly OutboxMetrics _metrics = new();
    private ServiceProvider? _services;

    public async Task InitializeAsync()
    {
        await fixture.ResetDatabaseAsync();
        await new OutboxMessageMigration().Up(fixture.Database);
        var services = new ServiceCollection();
        services.AddSingleton(fixture.Database);
        services.AddOutboxPersistence();
        _services = services.BuildServiceProvider();
    }

    public Task DisposeAsync()
    {
        _services?.Dispose();
        _metrics.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Process_Success_PublishesAndClearsPendingKey()
    {
        var message = CreateValidMessage(
            pendingKey: "runtime-cell:success:1:reconciliation");
        await fixture.DbContext.OutboxMessages.InsertOneAsync(message);
        var publisher = new RecordingPublisher();

        await CreateJob(publisher).ProcessOutboxMessagesAsync();

        var saved = await LoadAsync(message.Id);
        Assert.IsType<PublishedOutboxMessageState>(saved.State);
        Assert.Null(saved.PendingKey);
        var published = Assert.Single(publisher.Messages);
        Assert.IsType<RuntimeCellReconciliationRequestedEvent>(published);
    }

    [Fact]
    public async Task Process_PublishFailure_SchedulesRetryAndRetainsPendingKey()
    {
        const string pendingKey = "runtime-cell:retry:1:reconciliation";
        var message = CreateValidMessage(pendingKey);
        await fixture.DbContext.OutboxMessages.InsertOneAsync(message);
        var publisher = new RecordingPublisher(
            (_, _, _) => throw new InvalidOperationException("broker unavailable"));

        await CreateJob(publisher).ProcessOutboxMessagesAsync();

        var saved = await LoadAsync(message.Id);
        Assert.Equal(pendingKey, saved.PendingKey);
        var pending = Assert.IsType<PendingOutboxMessageState>(saved.State);
        Assert.Equal(1, pending.AttemptCount);
        Assert.True(pending.NextAttemptAtUtc > InitialNowUtc);
        Assert.Equal("broker unavailable", pending.LastError);
    }

    [Fact]
    public async Task Process_UnknownType_IsAbandonedAndDoesNotBlockValidMessage()
    {
        var unknown = CreateMessage(
            "unknown-event",
            "{}",
            "unknown-key",
            InitialNowUtc.AddMinutes(-2));
        var valid = CreateValidMessage(
            pendingKey: "valid-key",
            enqueuedAtUtc: InitialNowUtc.AddMinutes(-1));
        await fixture.DbContext.OutboxMessages.InsertManyAsync([unknown, valid]);
        var publisher = new RecordingPublisher();

        await CreateJob(publisher).ProcessOutboxMessagesAsync();

        var abandoned = await LoadAsync(unknown.Id);
        var abandonedState =
            Assert.IsType<AbandonedOutboxMessageState>(abandoned.State);
        Assert.Equal("UnknownEventType", abandonedState.FailureCode);
        Assert.Null(abandoned.PendingKey);
        Assert.IsType<PublishedOutboxMessageState>(
            (await LoadAsync(valid.Id)).State);
        Assert.Single(publisher.Messages);
    }

    [Fact]
    public async Task Process_InvalidPayload_IsAbandoned()
    {
        var message = CreateMessage(
            "runtime-cell-reconciliation-requested",
            "{ not-json",
            "invalid-payload-key",
            InitialNowUtc.AddMinutes(-1));
        await fixture.DbContext.OutboxMessages.InsertOneAsync(message);

        await CreateJob(new RecordingPublisher())
            .ProcessOutboxMessagesAsync();

        var saved = await LoadAsync(message.Id);
        var abandoned =
            Assert.IsType<AbandonedOutboxMessageState>(saved.State);
        Assert.Equal("InvalidPayload", abandoned.FailureCode);
        Assert.Null(saved.PendingKey);
    }

    [Fact]
    public async Task Process_NullEventCore_IsAbandonedAndDoesNotBlockBatch()
    {
        var invalid = CreateMessage(
            "runtime-cell-reconciliation-requested",
            $$"""
              {
                "runtimeCellId": "{{ObjectId.GenerateNewId()}}",
                "generation": 1,
                "eventCore": null
              }
              """,
            "null-core-key",
            InitialNowUtc.AddMinutes(-2));
        var valid = CreateValidMessage(
            pendingKey: "after-null-core-key",
            enqueuedAtUtc: InitialNowUtc.AddMinutes(-1));
        await fixture.DbContext.OutboxMessages.InsertManyAsync([invalid, valid]);
        var publisher = new RecordingPublisher();

        await CreateJob(publisher).ProcessOutboxMessagesAsync();

        var invalidSaved = await LoadAsync(invalid.Id);
        var abandoned =
            Assert.IsType<AbandonedOutboxMessageState>(invalidSaved.State);
        Assert.Equal("InvalidPayload", abandoned.FailureCode);
        Assert.IsType<PublishedOutboxMessageState>(
            (await LoadAsync(valid.Id)).State);
        Assert.Single(publisher.Messages);
    }

    [Fact]
    public async Task Process_ShutdownCancellation_DoesNotChangeMessageState()
    {
        var message = CreateValidMessage(pendingKey: "cancelled-key");
        await fixture.DbContext.OutboxMessages.InsertOneAsync(message);
        using var cancellation = new CancellationTokenSource();
        var publisher = new RecordingPublisher((_, _, cancellationToken) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(cancellationToken);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateJob(publisher)
                .ProcessOutboxMessagesAsync(cancellation.Token));

        var saved = await LoadAsync(message.Id);
        var pending = Assert.IsType<PendingOutboxMessageState>(saved.State);
        Assert.Equal(0, pending.AttemptCount);
        Assert.Equal("cancelled-key", saved.PendingKey);
    }

    [Fact]
    public async Task Process_NotYetDue_DoesNotPublish()
    {
        var message = CreateValidMessage(pendingKey: "future-key")
            .ScheduleRetry(InitialNowUtc.AddMinutes(1), "first failure");
        await fixture.DbContext.OutboxMessages.InsertOneAsync(message);
        var publisher = new RecordingPublisher();

        await CreateJob(publisher).ProcessOutboxMessagesAsync();

        Assert.Empty(publisher.Messages);
        Assert.Equal(message, await LoadAsync(message.Id));
    }

    [Fact]
    public async Task Process_RepeatedTransportFailures_NeverAbandonsMessage()
    {
        var message = CreateValidMessage(pendingKey: "persistent-key");
        await fixture.DbContext.OutboxMessages.InsertOneAsync(message);
        var publisher = new RecordingPublisher(
            (_, _, _) => throw new InvalidOperationException("offline"));
        var job = CreateJob(publisher);

        for (var attempt = 1; attempt <= 8; attempt++)
        {
            await job.ProcessOutboxMessagesAsync();
            var saved = await LoadAsync(message.Id);
            var pending = Assert.IsType<PendingOutboxMessageState>(saved.State);
            Assert.Equal(attempt, pending.AttemptCount);
            _timeProvider.SetUtcNow(pending.NextAttemptAtUtc);
        }

        var final = await LoadAsync(message.Id);
        Assert.IsType<PendingOutboxMessageState>(final.State);
        Assert.Equal("persistent-key", final.PendingKey);
    }

    [Fact]
    public async Task Cleanup_RemovesOnlyExpiredTerminalMessages()
    {
        var expiredPublished = CreateValidMessage(
                enqueuedAtUtc: InitialNowUtc.AddDays(-20))
            .MarkPublished(InitialNowUtc.AddDays(-10));
        var recentPublished = CreateValidMessage(
                enqueuedAtUtc: InitialNowUtc.AddDays(-2))
            .MarkPublished(InitialNowUtc.AddDays(-1));
        var expiredAbandoned = CreateValidMessage(
                enqueuedAtUtc: InitialNowUtc.AddDays(-40))
            .Abandon(
                InitialNowUtc.AddDays(-31),
                "InvalidPayload",
                "bad payload");
        var secondExpiredPublished = CreateValidMessage(
                enqueuedAtUtc: InitialNowUtc.AddDays(-21))
            .MarkPublished(InitialNowUtc.AddDays(-11));
        var thirdExpiredPublished = CreateValidMessage(
                enqueuedAtUtc: InitialNowUtc.AddDays(-22))
            .MarkPublished(InitialNowUtc.AddDays(-12));
        var pending = CreateValidMessage(
            pendingKey: "pending-key",
            enqueuedAtUtc: InitialNowUtc.AddDays(-60));
        await fixture.DbContext.OutboxMessages.InsertManyAsync(
        [
            expiredPublished,
            secondExpiredPublished,
            thirdExpiredPublished,
            recentPublished,
            expiredAbandoned,
            pending
        ]);

        await CreateJob(
                new RecordingPublisher(),
                new OutboxSettings { CleanupBatchSize = 2 })
            .CleanupMessagesAsync();

        var remainingIds = await fixture.DbContext.OutboxMessages
            .Find(Builders<OutboxMessage>.Filter.Empty)
            .Project(message => message.Id)
            .ToListAsync();
        Assert.DoesNotContain(expiredPublished.Id, remainingIds);
        Assert.DoesNotContain(secondExpiredPublished.Id, remainingIds);
        Assert.DoesNotContain(thirdExpiredPublished.Id, remainingIds);
        Assert.DoesNotContain(expiredAbandoned.Id, remainingIds);
        Assert.Contains(recentPublished.Id, remainingIds);
        Assert.Contains(pending.Id, remainingIds);
    }

    private OutboxProcessorJob CreateJob(
        IOutboxPublisher publisher,
        OutboxSettings? settings = null)
    {
        settings ??= new OutboxSettings();
        var services = _services ?? throw new InvalidOperationException(
            "Outbox persistence services were not initialized.");
        return new OutboxProcessorJob(
            services.GetRequiredService<LoadDueOutboxMessages>(),
            services.GetRequiredService<TryPersistOutboxMessageTransition>(),
            services.GetRequiredService<CleanupTerminalOutboxMessages>(),
            services.GetRequiredService<LoadOutboxQueueSnapshot>(),
            publisher,
            new OutboxEventAssemblyCatalog(
                [typeof(RuntimeCellReconciliationRequestedEvent).Assembly]),
            Options.Create(settings),
            _timeProvider,
            new OutboxRetryDelayPolicy(settings),
            _metrics,
            NullLogger<OutboxProcessorJob>.Instance);
    }

    private async Task<OutboxMessage> LoadAsync(string id) =>
        await fixture.DbContext.OutboxMessages
            .Find(message => message.Id == id)
            .SingleAsync();

    private static OutboxMessage CreateValidMessage(
        string? pendingKey = null,
        DateTime? enqueuedAtUtc = null)
    {
        var enqueued = enqueuedAtUtc ?? InitialNowUtc.AddMinutes(-1);
        var @event = new RuntimeCellReconciliationRequestedEvent(
            ObjectId.GenerateNewId().ToString(),
            1)
        {
            EventCore = new EventCore
            {
                TenantId = TestData.TenantId,
                OccurredOn = enqueued.AddSeconds(-1),
                TraceId = "0123456789abcdef0123456789abcdef",
                CorrelationId = "outbox-processor-test"
            }
        };
        var payload = JsonSerializer.Serialize(
            @event,
            @event.GetType(),
            EventExtensions.SerializerOptions);

        return CreateMessage(
            "runtime-cell-reconciliation-requested",
            payload,
            pendingKey,
            enqueued);
    }

    private static OutboxMessage CreateMessage(
        string eventType,
        string payload,
        string? pendingKey,
        DateTime enqueuedAtUtc) =>
        OutboxMessageFactory.Create(
            eventType,
            TestData.TenantId,
            payload,
            "0123456789abcdef0123456789abcdef",
            "outbox-processor-test",
            enqueuedAtUtc.AddSeconds(-1),
            pendingKey,
            new OutboxMessageFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                enqueuedAtUtc));

    private sealed class RecordingPublisher(
        Func<object, Type, CancellationToken, Task>? publish = null)
        : IOutboxPublisher
    {
        public List<object> Messages { get; } = [];

        public async Task PublishAsync(
            object message,
            Type messageType,
            CancellationToken cancellationToken)
        {
            if (publish is not null)
            {
                await publish(message, messageType, cancellationToken);
                return;
            }

            Messages.Add(message);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now)
        : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void SetUtcNow(DateTime utcNow) =>
            _now = new DateTimeOffset(
                DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
    }
}
