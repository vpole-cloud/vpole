using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Kubernetes;
using Pole.Features.ResourceGroups.Application.RuntimeCells;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Events;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;
using EventJson = Pole.Features.Events.Abstractions.EventExtensions;

namespace Pole.Tests.Integration.ResourceGroups;

[Collection(IntegrationTestCollection.Tenant3)]
public sealed class RuntimeCellDeletionProgressTests(MongoReplicaSetFixture fixture)
    : RuntimeCellLifecycleTestBase(fixture)
{
    [Theory]
    [InlineData("early")]
    [InlineData("tenant")]
    [InlineData("generation")]
    [InlineData("sequence")]
    [InlineData("due")]
    public async Task ScheduledCheck_RejectsEarlyOrStaleAuthority(string mismatch)
    {
        var pending = await InsertPendingAsync();
        var state = Assert.IsType<DeletingRuntimeCellState>(pending.State);
        var message = pending.ToDeletionRequestedEvent();
        message = mismatch switch
        {
            "tenant" => message with { EventCore = EventCore.Create(ObjectId.GenerateNewId().ToString()) },
            "generation" => message with { Generation = message.Generation + 1 },
            "sequence" => message with { CheckSequence = message.CheckSequence + 1 },
            "due" => message with { NotBeforeUtc = message.NotBeforeUtc!.Value.AddSeconds(1) },
            _ => message
        };
        var now = mismatch == "early" ? state.NextCheckAtUtc!.Value.AddTicks(-1) : state.NextCheckAtUtc!.Value;
        var manager = new RecordingNamespaceManager(Unit.Value, Unit.Value);

        await Progress(now, manager).HandleAsync(message);

        Assert.Equal(0, manager.DeleteCallCount);
        Assert.Equal(pending, await DbContext.RuntimeCells.Find(cell => cell.Id == pending.Id).SingleAsync());
    }

    [Fact]
    public async Task DueCheck_DeletesAndReleasesReferenceExactlyOnce()
    {
        var pending = await InsertPendingAsync();
        var due = ((DeletingRuntimeCellState)pending.State).NextCheckAtUtc!.Value;
        var admission = new RecordingReferenceAdmission();
        var manager = new RecordingNamespaceManager(Unit.Value, Unit.Value);
        var progress = Progress(due, manager, admission);
        var message = pending.ToDeletionRequestedEvent();

        await progress.HandleAsync(message);
        await progress.HandleAsync(message);
        await progress.HandleAsync(DeletionEvent(pending));

        Assert.IsType<DeletedRuntimeCellState>((await DbContext.RuntimeCells.Find(cell => cell.Id == pending.Id).SingleAsync()).State);
        Assert.Equal(1, manager.DeleteCallCount);
        Assert.Equal(1, admission.UnregisterCallCount);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync(Data.TenantId, "pole.resource-groups/v1/runtime-cell-deleted"));
    }

    [Fact]
    public async Task DuePendingCheck_AdvancesSequenceWithoutGenerationOrFailureBackoff()
    {
        var pending = await InsertPendingAsync();
        var due = ((DeletingRuntimeCellState)pending.State).NextCheckAtUtc!.Value;
        var manager = new RecordingNamespaceManager(Unit.Value, new RuntimeCellNamespaceDeletionPending(pending.KubernetesNamespace));
        var progress = Progress(due, manager);
        var message = pending.ToDeletionRequestedEvent() with
        {
            EventCore = EventCore.Create(Data.TenantId) with
            {
                TraceId = "0123456789abcdef0123456789abcdef",
                CorrelationId = "scheduled-native-progress"
            }
        };

        await progress.HandleAsync(message);
        await progress.HandleAsync(message);

        var saved = await DbContext.RuntimeCells.Find(cell => cell.Id == pending.Id).SingleAsync();
        var state = Assert.IsType<DeletingRuntimeCellState>(saved.State);
        Assert.Equal(pending.DesiredGeneration, saved.DesiredGeneration);
        Assert.Equal(2, state.CheckSequence);
        Assert.Equal(due.AddSeconds(5), state.NextCheckAtUtc);
        Assert.Equal(1, manager.DeleteCallCount);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync(Data.TenantId, "pole.resource-groups/v1/runtime-cell-deletion-requested"));
        var row = await DbContext.OutboxMessages.Find(row => row.TenantId == Data.TenantId &&
            row.EventType == "pole.resource-groups/v1/runtime-cell-deletion-requested").SingleAsync();
        var next = System.Text.Json.JsonSerializer.Deserialize<RuntimeCellDeletionRequestedEvent>(row.PayloadJson, EventJson.SerializerOptions)!;
        Assert.Equal(message.EventCore.TraceId, next.EventCore.TraceId);
        Assert.Equal(message.EventCore.CorrelationId, next.EventCore.CorrelationId);
    }

    [Fact]
    public async Task ConcurrentPendingResults_AdvanceOneSequenceAndWriteOneAtomicRequest()
    {
        var deleting = (await InsertCellAsync()).RequestDeletion(InitialTime.AddMinutes(1));
        await DbContext.RuntimeCells.ReplaceOneAsync(cell => cell.Id == deleting.Id, deleting);
        var persistence = CreateLifecyclePersistence(InitialTime.AddMinutes(2));

        await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => persistence.PersistDeletionAsync(deleting,
            deleting.DesiredGeneration, new RuntimeCellNamespaceDeletionPending(deleting.KubernetesNamespace))));

        var saved = await DbContext.RuntimeCells.Find(cell => cell.Id == deleting.Id).SingleAsync();
        var state = Assert.IsType<DeletingRuntimeCellState>(saved.State);
        Assert.Equal(deleting.DesiredGeneration, saved.DesiredGeneration);
        Assert.Equal(1, state.CheckSequence);
        Assert.Equal(InitialTime.AddMinutes(2).AddSeconds(5), state.NextCheckAtUtc);
        var row = Assert.Single(await DbContext.OutboxMessages.Find(row => row.TenantId == Data.TenantId &&
            row.EventType == "pole.resource-groups/v1/runtime-cell-deletion-requested").ToListAsync());
        var request = System.Text.Json.JsonSerializer.Deserialize<RuntimeCellDeletionRequestedEvent>(row.PayloadJson, EventJson.SerializerOptions)!;
        Assert.Equal(state.NextCheckAtUtc, request.NotBeforeUtc);
        Assert.Equal(state.CheckSequence, request.CheckSequence);
        Assert.Equal(RuntimeCellPendingKey.DeletionCheck(saved.Id, saved.DesiredGeneration, state.CheckSequence), row.PendingKey);
    }

    [Fact]
    public async Task OutboxFailure_RollsBackPendingState()
    {
        var deleting = (await InsertCellAsync()).RequestDeletion(InitialTime.AddMinutes(1));
        await DbContext.RuntimeCells.ReplaceOneAsync(cell => cell.Id == deleting.Id, deleting);
        var expected = new InvalidOperationException("Controlled outbox failure.");
        var persistence = new RuntimeCellLifecyclePersistence(DbContext.RuntimeCells,
            (_, _, _) => throw expected, NoOpReferenceChanges,
            new MongoRunTransaction(Fixture.Client).InvokeOutcomeAsync<Unit>,
            new MongoRunTransaction(Fixture.Client).InvokeOutcomeAsync<bool>,
            new FixedTimeProvider(InitialTime.AddMinutes(2)));

        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => persistence.PersistDeletionAsync(
            deleting, deleting.DesiredGeneration, new RuntimeCellNamespaceDeletionPending(deleting.KubernetesNamespace))));
        Assert.Equal(deleting, await DbContext.RuntimeCells.Find(cell => cell.Id == deleting.Id).SingleAsync());
    }

    [Fact]
    public async Task OwnershipConflict_RemainsARealFailureAndDoesNotReleaseReference()
    {
        var pending = await InsertPendingAsync();
        var due = ((DeletingRuntimeCellState)pending.State).NextCheckAtUtc!.Value;
        var admission = new RecordingReferenceAdmission();
        var manager = new RecordingNamespaceManager(Unit.Value, new RuntimeCellNamespaceOwnershipConflict("safe-owned-name"));

        await Progress(due, manager, admission).HandleAsync(pending.ToDeletionRequestedEvent());

        var saved = await DbContext.RuntimeCells.Find(cell => cell.Id == pending.Id).SingleAsync();
        Assert.Equal("NamespaceOwnershipConflict", Assert.IsType<FailedRuntimeCellState>(saved.State).FailureCode);
        Assert.Equal(0, admission.UnregisterCallCount);
    }

    [Fact]
    public async Task CancelledNativeCheck_PreservesPendingStateAndReference()
    {
        var pending = await InsertPendingAsync();
        var due = ((DeletingRuntimeCellState)pending.State).NextCheckAtUtc!.Value;
        using var cancellation = new CancellationTokenSource();
        var admission = new RecordingReferenceAdmission();
        var progress = new RuntimeCellDeletionProgress(CreateLifecyclePersistence(due, admission.ApplyChangesOutcomeAsync),
            (_, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult<RuntimeCellDeleteOutcome>(Unit.Value); },
            CreateDeletionFinalizer(due), new FixedTimeProvider(due));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => progress.HandleAsync(pending.ToDeletionRequestedEvent(), cancellation.Token));

        Assert.Equal(pending, await DbContext.RuntimeCells.Find(cell => cell.Id == pending.Id).SingleAsync());
        Assert.Equal(0, admission.UnregisterCallCount);
    }

    [Fact]
    public async Task RejectedReferenceRelease_RollsBackDeletedStateAndEvent()
    {
        var pending = await InsertPendingAsync();
        var due = ((DeletingRuntimeCellState)pending.State).NextCheckAtUtc!.Value;
        var persistence = CreateLifecyclePersistence(due, (request, _, _) =>
            Task.FromResult<ApplyDeploymentTargetReferenceChangesOutcome>(
                new DeploymentTargetReferenceChangeRejected(request.Owner, [pending.DeploymentTargetId])));

        Assert.False(await persistence.PersistDeletionAsync(pending, pending.DesiredGeneration, Unit.Value));

        Assert.Equal(pending, await DbContext.RuntimeCells.Find(cell => cell.Id == pending.Id).SingleAsync());
        Assert.Equal(0L, await DbContext.CountOutboxMessagesAsync(Data.TenantId, "pole.resource-groups/v1/runtime-cell-deleted"));
    }

    [Fact]
    public async Task LegacyDeletingDocument_CanAdvanceThroughTheMissingSequenceFence()
    {
        var deleting = (await InsertCellAsync()).RequestDeletion(InitialTime.AddMinutes(1));
        var legacy = deleting.ToBsonDocument();
        legacy["State"].AsBsonDocument.Remove("NextCheckAtUtc");
        legacy["State"].AsBsonDocument.Remove("CheckSequence");
        await DbContext.Database.GetCollection<BsonDocument>("RuntimeCells").ReplaceOneAsync(
            document => document["_id"] == ObjectId.Parse(deleting.Id), legacy);
        var loaded = await DbContext.RuntimeCells.Find(cell => cell.Id == deleting.Id).SingleAsync();

        await CreateLifecyclePersistence(InitialTime.AddMinutes(2)).PersistDeletionAsync(loaded,
            loaded.DesiredGeneration, new RuntimeCellNamespaceDeletionPending(loaded.KubernetesNamespace));

        var saved = await DbContext.RuntimeCells.Find(cell => cell.Id == deleting.Id).SingleAsync();
        Assert.Equal(1, Assert.IsType<DeletingRuntimeCellState>(saved.State).CheckSequence);
    }

    private async Task<RuntimeCell> InsertPendingAsync()
    {
        var cell = (await InsertCellAsync()).RequestDeletion(InitialTime.AddMinutes(1));
        var pending = cell.ScheduleDeletionCheck(cell.DesiredGeneration, InitialTime.AddMinutes(2), InitialTime.AddMinutes(2).AddSeconds(5));
        await DbContext.RuntimeCells.ReplaceOneAsync(value => value.Id == cell.Id, pending);
        return pending;
    }

    private RuntimeCellDeletionProgress Progress(DateTime now, RecordingNamespaceManager manager, RecordingReferenceAdmission? admission = null) =>
        new(CreateLifecyclePersistence(now, admission is null ? null : admission.ApplyChangesOutcomeAsync), manager.DeleteAsync,
            CreateDeletionFinalizer(now), new FixedTimeProvider(now));
}

[Collection(IntegrationTestCollection.Global2)]
public sealed class RuntimeCellDeletionRecoveryTests(MongoReplicaSetFixture fixture)
    : RuntimeCellLifecycleTestBase(fixture, true), IIsolatedIntegrationTest
{
    [Fact]
    public async Task Scanner_RecoversDueProgressWithoutWaitingForThirtySecondStaleness()
    {
        var cell = (await InsertCellAsync()).RequestDeletion(InitialTime.AddMinutes(1));
        var checkedAt = InitialTime.AddMinutes(2);
        var due = checkedAt.AddSeconds(5);
        var pending = cell.ScheduleDeletionCheck(cell.DesiredGeneration, checkedAt, due);
        await DbContext.RuntimeCells.ReplaceOneAsync(value => value.Id == cell.Id, pending);
        var persistence = new RuntimeCellRecoveryPersistence(DbContext.IsolationScopes, DbContext.RuntimeCells,
            StoreOutboxWrites, new MongoRunTransaction(Fixture.Client).InvokeOutcomeAsync<Unit>);

        Assert.Empty(await persistence.LoadRecoveryCandidatesAsync(due.AddSeconds(-30), due.AddTicks(-1), 100));
        Assert.Equal(pending.Id, Assert.Single(await persistence.LoadRecoveryCandidatesAsync(due.AddSeconds(-30), due, 100)).Id);
        await persistence.RecoverAsync(pending.Id, due);
        await persistence.RecoverAsync(pending.Id, due);

        Assert.Equal(pending, await DbContext.RuntimeCells.Find(value => value.Id == pending.Id).SingleAsync());
        var row = Assert.Single(await DbContext.OutboxMessages.Find(row =>
            row.EventType == "pole.resource-groups/v1/runtime-cell-deletion-requested").ToListAsync());
        Assert.Equal(RuntimeCellPendingKey.DeletionCheck(pending.Id, pending.DesiredGeneration, 1), row.PendingKey);
    }
}
