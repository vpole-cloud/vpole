using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Consumers;
using Pole.Features.ResourceGroups.Application.Jobs;
using Pole.Features.ResourceGroups.Application.Kubernetes;
using Pole.Features.ResourceGroups.Application.Mongo;
using Pole.Features.ResourceGroups.Application.References;
using Pole.Features.ResourceGroups.Application.ResourceGroups;
using Pole.Features.ResourceGroups.Application.RuntimeCells;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Events;
using Pole.Features.Shared.Models;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ResourceGroups;

public sealed class RuntimeCellLifecycleIntegrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private static readonly DateTime InitialTime =
        new(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ReconciliationSuccess_MarksReady_AndDuplicateEventIsNoOp()
    {
        var cell = await InsertCellAsync();
        var manager = new RecordingNamespaceManager(Unit.Value, Unit.Value);
        var consumer = new ReconcileRuntimeCellOnReconciliationRequestedConsumer(
            CreateLifecyclePersistence(InitialTime.AddMinutes(1)),
            manager.EnsureCreatedAsync);

        await consumer.HandleAsync(ReconciliationEvent(cell));
        await consumer.HandleAsync(ReconciliationEvent(cell));

        var saved = await DbContext.RuntimeCells.Find(item => item.Id == cell.Id).SingleAsync();
        Assert.IsType<ReadyRuntimeCellState>(saved.State);
        Assert.Equal(1, saved.ObservedGeneration);
        Assert.Equal(1, manager.EnsureCallCount);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("pole.resource-groups/v1/runtime-cell-ready"));
    }

    [Fact]
    public async Task ReconciliationOwnershipConflict_PersistsSafeRetryableFailure()
    {
        var cell = await InsertCellAsync();
        const string providerSecret = "provider-secret-must-not-be-persisted";
        var manager = new RecordingNamespaceManager(
            new RuntimeCellNamespaceOwnershipConflict(providerSecret),
            Unit.Value);
        var consumer = new ReconcileRuntimeCellOnReconciliationRequestedConsumer(
            CreateLifecyclePersistence(InitialTime.AddMinutes(1)),
            manager.EnsureCreatedAsync);

        await consumer.HandleAsync(ReconciliationEvent(cell));

        var saved = await DbContext.RuntimeCells.Find(item => item.Id == cell.Id).SingleAsync();
        var failed = Assert.IsType<FailedRuntimeCellState>(saved.State);
        Assert.Equal("NamespaceOwnershipConflict", failed.FailureCode);
        Assert.DoesNotContain(providerSecret, failed.FailureMessage, StringComparison.Ordinal);
        Assert.True(failed.RetryNotBeforeUtc > failed.AttemptedAtUtc);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync(
            "pole.resource-groups/v1/runtime-cell-reconciliation-failed"));
    }

    [Fact]
    public async Task ReconciliationPolicyApplyFailure_KeepsCellNonReadyAndRetryable()
    {
        var cell = await InsertCellAsync();
        var manager = new RecordingNamespaceManager(
            new RuntimeCellNetworkPolicyUnavailable(cell.KubernetesNamespace),
            Unit.Value);
        var consumer = new ReconcileRuntimeCellOnReconciliationRequestedConsumer(
            CreateLifecyclePersistence(InitialTime.AddMinutes(1)),
            manager.EnsureCreatedAsync);

        await consumer.HandleAsync(ReconciliationEvent(cell));

        var saved = await DbContext.RuntimeCells.Find(item => item.Id == cell.Id).SingleAsync();
        var failed = Assert.IsType<FailedRuntimeCellState>(saved.State);
        Assert.Equal("NetworkPolicyApplyFailed", failed.FailureCode);
        Assert.Contains("ingress policy", failed.FailureMessage, StringComparison.Ordinal);
        Assert.True(failed.RetryNotBeforeUtc > failed.AttemptedAtUtc);
        Assert.Equal(0L, await DbContext.CountOutboxMessagesAsync(
            "pole.resource-groups/v1/runtime-cell-ready"));
    }

    [Fact]
    public async Task DeletionSuccess_MarksDeleted_UnregistersReference_AndIsIdempotent()
    {
        var provisioning = await InsertCellAsync();
        var deleting = provisioning.RequestDeletion(InitialTime.AddMinutes(1));
        await DbContext.RuntimeCells.ReplaceOneAsync(cell => cell.Id == provisioning.Id, deleting);
        var admission = new RecordingReferenceAdmission();
        var manager = new RecordingNamespaceManager(Unit.Value, Unit.Value);
        var consumer = new DeleteRuntimeCellOnDeletionRequestedConsumer(
            CreateLifecyclePersistence(InitialTime.AddMinutes(2), admission.ApplyChangesOutcomeAsync),
            manager.DeleteAsync,
            CreateDeletionFinalizer(InitialTime.AddMinutes(2)));

        await consumer.HandleAsync(DeletionEvent(deleting));
        await consumer.HandleAsync(DeletionEvent(deleting));

        var saved = await DbContext.RuntimeCells.Find(item => item.Id == deleting.Id).SingleAsync();
        Assert.IsType<DeletedRuntimeCellState>(saved.State);
        Assert.Equal(deleting.DesiredGeneration, saved.ObservedGeneration);
        Assert.Equal(1, manager.DeleteCallCount);
        Assert.Equal(1, admission.UnregisterCallCount);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("pole.resource-groups/v1/runtime-cell-deleted"));
    }

    [Fact]
    public async Task DeletionPending_KeepsTargetReferenceAndPersistsRetryableFailure()
    {
        var provisioning = await InsertCellAsync();
        var deleting = provisioning.RequestDeletion(InitialTime.AddMinutes(1));
        await DbContext.RuntimeCells.ReplaceOneAsync(cell => cell.Id == provisioning.Id, deleting);
        var admission = new RecordingReferenceAdmission();
        var manager = new RecordingNamespaceManager(
            Unit.Value,
            new RuntimeCellNamespaceDeletionPending("pole-runtime-cell"));
        var consumer = new DeleteRuntimeCellOnDeletionRequestedConsumer(
            CreateLifecyclePersistence(InitialTime.AddMinutes(2), admission.ApplyChangesOutcomeAsync),
            manager.DeleteAsync,
            CreateDeletionFinalizer(InitialTime.AddMinutes(2)));

        await consumer.HandleAsync(DeletionEvent(deleting));

        var saved = await DbContext.RuntimeCells.Find(item => item.Id == deleting.Id).SingleAsync();
        var failed = Assert.IsType<FailedRuntimeCellState>(saved.State);
        Assert.Equal(RuntimeCellOperation.Deletion, failed.Operation);
        Assert.Equal(0, admission.UnregisterCallCount);
        Assert.Equal(0L, await DbContext.CountOutboxMessagesAsync("pole.resource-groups/v1/runtime-cell-deleted"));
    }

    [Fact]
    public async Task DeletionSuccess_FinalizesDeletingResourceGroupAfterLastCell()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var requestedAt = resourceGroup.UpdatedAtUtc.AddMinutes(1);
        await DbContext.ResourceGroups.ReplaceOneAsync(
            group => group.Id == resourceGroup.Id,
            resourceGroup.RequestDeletion(requestedAt));
        var defaultScope = await DbContext.IsolationScopes
            .Find(scope => scope.Id == resourceGroup.DefaultIsolationScopeId)
            .SingleAsync();
        await DbContext.IsolationScopes.ReplaceOneAsync(
            scope => scope.Id == defaultScope.Id,
            defaultScope.RequestResourceGroupDeletion(requestedAt));
        var cell = RuntimeCellFactory.Create(
                resourceGroup.TenantId,
                resourceGroup.Id,
                defaultScope.Id,
                "507f1f77bcf86cd799439071",
                "pole-finalize-resource-group",
                new RuntimeCellFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), requestedAt))
            .RequestDeletion(requestedAt.AddMinutes(1));
        await DbContext.RuntimeCells.InsertOneAsync(cell);
        var completedAt = requestedAt.AddMinutes(2);
        var consumer = new DeleteRuntimeCellOnDeletionRequestedConsumer(
            CreateLifecyclePersistence(
                completedAt,
                new RecordingReferenceAdmission().ApplyChangesOutcomeAsync),
            new RecordingNamespaceManager(Unit.Value, Unit.Value).DeleteAsync,
            CreateDeletionFinalizer(completedAt));

        await consumer.HandleAsync(DeletionEvent(cell));

        Assert.False(await DbContext.ResourceGroups
            .Find(group => group.Id == resourceGroup.Id)
            .AnyAsync());
        Assert.False(await DbContext.IsolationScopes
            .Find(scope => scope.ResourceGroupId == resourceGroup.Id)
            .AnyAsync());
        Assert.False(await DbContext.RuntimeCells
            .Find(item => item.ResourceGroupId == resourceGroup.Id)
            .AnyAsync());
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("pole.resource-groups/v1/runtime-cell-deleted"));
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("pole.resource-groups/v1/resource-group-deleted"));
    }

    [Fact]
    public async Task Recovery_AdvancesDueFailure_AndOnlyWritesAnOutboxRequest()
    {
        var cell = await InsertCellAsync();
        var attemptedAt = InitialTime.AddMinutes(1);
        var failed = cell.MarkFailed(
            cell.DesiredGeneration,
            RuntimeCellOperation.Reconciliation,
            "NamespaceOperationFailed",
            "Safe failure.",
            attemptedAt,
            attemptedAt.AddSeconds(30));
        await DbContext.RuntimeCells.ReplaceOneAsync(item => item.Id == cell.Id, failed);
        var job = CreateRecoveryJob(InitialTime.AddMinutes(2));

        await job.RecoverAsync();

        var recovered = await DbContext.RuntimeCells.Find(item => item.Id == cell.Id).SingleAsync();
        Assert.IsType<ProvisioningRuntimeCellState>(recovered.State);
        Assert.Equal(cell.DesiredGeneration + 1, recovered.DesiredGeneration);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync(
            "pole.resource-groups/v1/runtime-cell-reconciliation-requested"));
        var pendingRequest = await DbContext.OutboxMessages
            .Find(message => message.EventType == "pole.resource-groups/v1/runtime-cell-reconciliation-requested")
            .SingleAsync();
        Assert.Equal(
            RuntimeCellPendingKey.Reconciliation(recovered.Id, recovered.DesiredGeneration),
            pendingRequest.PendingKey);
        Assert.IsType<PendingOutboxMessageState>(pendingRequest.State);

        await DbContext.OutboxMessages.ReplaceOneAsync(
            message => message.Id == pendingRequest.Id,
            pendingRequest with { PayloadJson = "{ malformed payload" });

        await CreateRecoveryJob(InitialTime.AddMinutes(3)).RecoverAsync();

        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync(
            "pole.resource-groups/v1/runtime-cell-reconciliation-requested"));
    }

    [Fact]
    public async Task Recovery_FinalizesDeletionAfterDeletedCellCrashWindow()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var requestedAt = resourceGroup.UpdatedAtUtc.AddMinutes(1);
        await DbContext.ResourceGroups.ReplaceOneAsync(
            group => group.Id == resourceGroup.Id,
            resourceGroup.RequestDeletion(requestedAt));
        var defaultScope = await DbContext.IsolationScopes
            .Find(scope => scope.Id == resourceGroup.DefaultIsolationScopeId)
            .SingleAsync();
        await DbContext.IsolationScopes.ReplaceOneAsync(
            scope => scope.Id == defaultScope.Id,
            defaultScope.RequestResourceGroupDeletion(requestedAt));
        var deleting = RuntimeCellFactory.Create(
                resourceGroup.TenantId,
                resourceGroup.Id,
                defaultScope.Id,
                "507f1f77bcf86cd799439071",
                "pole-recovery-finalize",
                new RuntimeCellFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), requestedAt))
            .RequestDeletion(requestedAt.AddMinutes(1));
        var deleted = deleting.MarkDeleted(deleting.DesiredGeneration, requestedAt.AddMinutes(2));
        await DbContext.RuntimeCells.InsertOneAsync(deleted);
        var recoveryTime = requestedAt.AddMinutes(3);

        await CreateRecoveryJob(recoveryTime).RecoverAsync();

        Assert.False(await DbContext.ResourceGroups
            .Find(group => group.Id == resourceGroup.Id)
            .AnyAsync());
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("pole.resource-groups/v1/resource-group-deleted"));
    }

    private async Task<RuntimeCell> InsertCellAsync()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var cell = RuntimeCellFactory.Create(
            resourceGroup.TenantId,
            resourceGroup.Id,
            resourceGroup.DefaultIsolationScopeId,
            "507f1f77bcf86cd799439071",
            "pole-runtime-cell",
            new RuntimeCellFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), InitialTime));
        await DbContext.RuntimeCells.InsertOneAsync(cell);
        return cell;
    }

    private RuntimeCellLifecyclePersistence CreateLifecyclePersistence(
        DateTime utcNow,
        ApplyDeploymentTargetReferenceChanges? applyReferenceChanges = null) =>
        new(
            DbContext.RuntimeCells,
            StoreOutboxWrites,
            applyReferenceChanges ?? NoOpReferenceChanges,
            new MongoRunTransaction(Fixture.Client).InvokeOutcomeAsync<Unit>,
            new MongoRunTransaction(Fixture.Client).InvokeOutcomeAsync<bool>,
            new FixedTimeProvider(utcNow));

    private RuntimeCellRecoveryJob CreateRecoveryJob(DateTime utcNow)
    {
        var transaction = new MongoRunTransaction(Fixture.Client);
        var persistence = new RuntimeCellRecoveryPersistence(
            DbContext.IsolationScopes,
            DbContext.RuntimeCells,
            StoreOutboxWrites,
            transaction.InvokeOutcomeAsync<Unit>);
        return new RuntimeCellRecoveryJob(
            persistence,
            CreateDeletionFinalizer(utcNow),
            new FixedTimeProvider(utcNow));
    }

    private FinalizeResourceGroupDeletion CreateDeletionFinalizer(DateTime utcNow) =>
        Services.GetRequiredService<FinalizeResourceGroupDeletion>();

    private Task StoreOutboxWrites(
        IClientSessionHandle session,
        IReadOnlyCollection<OutboxWrite> writes,
        CancellationToken cancellationToken = default) =>
        Fixture.StoreOutboxWrites(session, writes, cancellationToken);

    private static Task<ApplyDeploymentTargetReferenceChangesOutcome>
        NoOpReferenceChanges(
        ApplyDeploymentTargetReferenceChangesRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ApplyDeploymentTargetReferenceChangesOutcome>(
            new DeploymentTargetReferencesChanged());

    private static RuntimeCellReconciliationRequestedEvent ReconciliationEvent(RuntimeCell cell) =>
        new(cell.Id, cell.DesiredGeneration)
        {
            EventCore = EventCore.Create(cell.TenantId)
        };

    private static RuntimeCellDeletionRequestedEvent DeletionEvent(RuntimeCell cell) =>
        new(cell.Id, cell.DesiredGeneration)
        {
            EventCore = EventCore.Create(cell.TenantId)
        };

    private sealed class RecordingNamespaceManager(
        RuntimeCellEnsureOutcome ensureResult,
        RuntimeCellDeleteOutcome deleteResult)
    {
        public int EnsureCallCount { get; private set; }
        public int DeleteCallCount { get; private set; }

        public Task<RuntimeCellEnsureOutcome> EnsureCreatedAsync(
            RuntimeCell runtimeCell,
            CancellationToken cancellationToken = default)
        {
            EnsureCallCount++;
            return Task.FromResult(ensureResult);
        }

        public Task<RuntimeCellDeleteOutcome> DeleteAsync(
            RuntimeCell runtimeCell,
            CancellationToken cancellationToken = default)
        {
            DeleteCallCount++;
            return Task.FromResult(deleteResult);
        }
    }

    private sealed class RecordingReferenceAdmission
    {
        public int UnregisterCallCount { get; private set; }

        public Task<ApplyDeploymentTargetReferenceChangesOutcome>
            ApplyChangesOutcomeAsync(
            ApplyDeploymentTargetReferenceChangesRequest request,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
        {
            UnregisterCallCount += request.RemovedDeploymentTargetIds.Count;
            return Task.FromResult<ApplyDeploymentTargetReferenceChangesOutcome>(
                new DeploymentTargetReferencesChanged());
        }
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
