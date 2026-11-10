using System.Reflection;
using System.Text.Json;
using Hangfire;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Application;
using Pole.Features.Outbox.Migrations;
using Pole.Features.ResourceGroups.Events;
using Pole.Tests.Integration.Infrastructure;
using Xunit;
using EventExtensions = Pole.Features.Events.Abstractions.EventExtensions;

namespace Pole.Tests.Integration.Outbox;

[Collection(IntegrationTestCollection.Global4)]
public sealed class OutboxDispatchConcurrencyTests(
    MongoReplicaSetFixture fixture
) : MongoIntegrationTestBase(fixture)
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await new OutboxMessageMigration().Up(Fixture.Database);
    }

    public override Task DisposeAsync() => base.DisposeAsync();

    [Fact]
    public async Task Replicas_ShareTheProcessingLockAndReleaseItAfterAsyncPublish()
    {
        var firstStorage = CreateStorage();
        var secondStorage = CreateStorage();
        var started = Signal();
        var release = Signal();
        var publishCount = 0;
        var publisher = new DelegatePublisher(async (
            _, _,
            cancellationToken) =>
        {
            if (Interlocked.Increment(ref publishCount) == 1)
            {
                started.SetResult();
                await release.Task.WaitAsync(cancellationToken);
            }

            // Ensure publishing crosses an async boundary while the lock is held.
            await Task.Yield();
        });
        await using var firstServices = CreateServices(firstStorage, publisher);
        await using var secondServices = CreateServices(secondStorage, publisher);
        var firstTrigger = firstServices.GetRequiredService<IOutboxProcessingTrigger>();
        var secondTrigger = secondServices.GetRequiredService<IOutboxProcessingTrigger>();
        var firstMessage = CreateMessage();
        await Fixture.DbContext.OutboxMessages.InsertOneAsync(firstMessage);

        var firstDispatch = firstTrigger.TriggerAsync(CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await secondTrigger.TriggerAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, Volatile.Read(ref publishCount));
        }
        finally
        {
            release.TrySetResult();
            await firstDispatch.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await AssertLockAvailableFromNewWorkerAsync(secondStorage);
        var secondMessage = CreateMessage();
        await Fixture.DbContext.OutboxMessages.InsertOneAsync(secondMessage);
        await secondTrigger.TriggerAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, publishCount);
        Assert.IsType<PublishedOutboxMessageState>((await LoadAsync(firstMessage.Id)).State);
        Assert.IsType<PublishedOutboxMessageState>((await LoadAsync(secondMessage.Id)).State);
        Assert.Equal(0, firstStorage.GetMonitoringApi().EnqueuedCount("default"));
        Assert.Equal(0, firstStorage.GetMonitoringApi().ScheduledCount());
    }

    [Fact]
    public async Task RecurringProcessorLock_SkipsDirectDispatchUntilReleased()
    {
        var cronStorage = CreateStorage();
        var directStorage = CreateStorage();
        var method = typeof(OutboxProcessorJob).GetMethod(nameof(OutboxProcessorJob.ProcessOutboxMessagesAsync))!;
        var filter = method.GetCustomAttribute<DisableConcurrentExecutionAttribute>()!;
        Assert.Equal(OutboxProcessingLock.Resource, filter.Resource);
        var locked = Signal();
        var release = Signal();
        var holdCronLock = Task.Run(() =>
        {
            using var connection = cronStorage.GetConnection();
            using var processingLock = connection.AcquireDistributedLock(
                filter.Resource!,
                TimeSpan.FromSeconds(filter.TimeoutSec));
            locked.SetResult();
            release.Task.GetAwaiter().GetResult();
        });
        var publishCount = 0;
        var publisher = new DelegatePublisher((
            _, _,
            _) =>
        {
            Interlocked.Increment(ref publishCount);
            return Task.CompletedTask;
        });
        await using var services = CreateServices(directStorage, publisher);
        var trigger = services.GetRequiredService<IOutboxProcessingTrigger>();
        var message = CreateMessage();
        await Fixture.DbContext.OutboxMessages.InsertOneAsync(message);

        try
        {
            await locked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await trigger.TriggerAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, publishCount);
            Assert.IsType<PendingOutboxMessageState>((await LoadAsync(message.Id)).State);
        }
        finally
        {
            release.TrySetResult();
            await holdCronLock.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await trigger.TriggerAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, publishCount);
    }

    [Fact]
    public async Task Cancellation_PreservesPendingStateAndReleasesTheLock()
    {
        var firstStorage = CreateStorage();
        var secondStorage = CreateStorage();
        var started = Signal();
        var count = 0;
        var publisher = new DelegatePublisher(async (
            _, _,
            cancellationToken) =>
        {
            if (Interlocked.Increment(ref count) == 1)
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        });
        await using var firstServices = CreateServices(firstStorage, publisher);
        await using var secondServices = CreateServices(secondStorage, publisher);
        using var cancellation = new CancellationTokenSource();
        var message = CreateMessage();
        await Fixture.DbContext.OutboxMessages.InsertOneAsync(message);
        var dispatch = firstServices.GetRequiredService<IOutboxProcessingTrigger>().TriggerAsync(cancellation.Token);

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
            }
        }

        await AssertLockAvailableFromNewWorkerAsync(secondStorage);
        var pending = Assert.IsType<PendingOutboxMessageState>((await LoadAsync(message.Id)).State);
        Assert.Equal(0, pending.AttemptCount);

        await secondServices.GetRequiredService<IOutboxProcessingTrigger>()
            .TriggerAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, count);
        Assert.IsType<PublishedOutboxMessageState>((await LoadAsync(message.Id)).State);
    }

    private static Task AssertLockAvailableFromNewWorkerAsync(JobStorage storage) => Task.Factory
        .StartNew(
            () =>
            {
                // Hangfire.Mongo tracks nested locks per thread. A dedicated worker
                // must reacquire the Mongo lock, so thread reuse cannot hide a leak.
                using var connection = storage.GetConnection();
                using var processingLock = connection.AcquireDistributedLock(
                    OutboxProcessingLock.Resource,
                    TimeSpan.Zero);
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default)
        .WaitAsync(TimeSpan.FromSeconds(5));

    private MongoStorage CreateStorage() => new(
        Fixture.Client,
        Fixture.Database.DatabaseNamespace.DatabaseName,
        new MongoStorageOptions
        {
            Prefix = "outbox-dispatch-tests",
            CheckConnection = false,
            MigrationOptions = new MongoMigrationOptions
            {
                MigrationStrategy = new MigrateMongoMigrationStrategy(),
                BackupStrategy = new CollectionMongoBackupStrategy(),
            },
        });

    private ServiceProvider CreateServices(JobStorage storage, IOutboxPublisher publisher)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Fixture.Database);
        services.AddSingleton(storage);
        services.AddSingleton(publisher);
        services.AddSingleton(TimeProvider.System);
        services.AddOutboxFeature(
            new ConfigurationBuilder().Build(),
            typeof(RuntimeCellReconciliationRequestedEvent).Assembly);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private Task<OutboxMessage> LoadAsync(string id) =>
        Fixture.DbContext.OutboxMessages.Find(message => message.Id == id).SingleAsync();

    private OutboxMessage CreateMessage()
    {
        var now = DateTime.UtcNow.AddSeconds(-1);
        var @event = new RuntimeCellReconciliationRequestedEvent(ObjectId.GenerateNewId().ToString(), 1)
        {
            EventCore = new EventCore { TenantId = Data.TenantId, OccurredOn = now },
        };
        return OutboxMessage.Create(
            "pole.resource-groups/v1/runtime-cell-reconciliation-requested",
            Data.TenantId,
            JsonSerializer.Serialize(@event, EventExtensions.SerializerOptions),
            @event.EventCore.TraceId,
            @event.EventCore.CorrelationId,
            now,
            now,
            pendingKey: ObjectId.GenerateNewId().ToString());
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class DelegatePublisher(
        Func<object, Type, CancellationToken, Task> publish
    ) : IOutboxPublisher
    {
        public Task PublishAsync(
            object message,
            Type messageType,
            CancellationToken cancellationToken) => publish(message, messageType, cancellationToken);
    }
}
