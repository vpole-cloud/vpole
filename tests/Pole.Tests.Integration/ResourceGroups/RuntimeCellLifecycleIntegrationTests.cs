using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Application.Consumers;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Jobs;
using Pole.Features.ResourceGroups.Application.Kubernetes;
using Pole.Features.ResourceGroups.Application.Services;
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
        var manager = new RecordingNamespaceManager(Result.Success(), Result.Success());
        var consumer = new ReconcileRuntimeCellOnReconciliationRequestedConsumer(
            DbContext,
            manager,
            new FixedTimeProvider(InitialTime.AddMinutes(1)));
        var message = ReconciliationEvent(cell);

        await consumer.HandleAsync(message);
        await consumer.HandleAsync(message);

        var saved = await DbContext.RuntimeCells.Find(item => item.Id == cell.Id).SingleAsync();
        Assert.IsType<ReadyRuntimeCellState>(saved.State);
        Assert.Equal(1, saved.ObservedGeneration);
        Assert.Equal(1, manager.EnsureCallCount);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("runtime-cell-ready"));
    }

    [Fact]
    public async Task ReconciliationOwnershipConflict_PersistsSafeRetryableFailure()
    {
        var cell = await InsertCellAsync();
        const string providerSecret = "provider-secret-must-not-be-persisted";
        var manager = new RecordingNamespaceManager(
            Failures.Conflict($"Ownership conflict: {providerSecret}"),
            Result.Success());
        var consumer = new ReconcileRuntimeCellOnReconciliationRequestedConsumer(
            DbContext,
            manager,
            new FixedTimeProvider(InitialTime.AddMinutes(1)));

        await consumer.HandleAsync(ReconciliationEvent(cell));

        var saved = await DbContext.RuntimeCells.Find(item => item.Id == cell.Id).SingleAsync();
        var failed = Assert.IsType<FailedRuntimeCellState>(saved.State);
        Assert.Equal("NamespaceOwnershipConflict", failed.FailureCode);
        Assert.DoesNotContain(providerSecret, failed.FailureMessage, StringComparison.Ordinal);
        Assert.True(failed.RetryNotBeforeUtc > failed.AttemptedAtUtc);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync(
            "runtime-cell-reconciliation-failed"));
    }

    [Fact]
    public async Task DeletionSuccess_MarksDeleted_UnregistersReference_AndIsIdempotent()
    {
        var provisioning = await InsertCellAsync();
        var deleting = provisioning.RequestDeletion(InitialTime.AddMinutes(1));
        await DbContext.RuntimeCells.ReplaceOneAsync(
            cell => cell.Id == provisioning.Id,
            deleting);
        var admission = new RecordingReferenceAdmission();
        var manager = new RecordingNamespaceManager(Result.Success(), Result.Success());
        var consumer = new DeleteRuntimeCellOnDeletionRequestedConsumer(
            DbContext,
            manager,
            admission,
            CreateDeletionFinalizer(InitialTime.AddMinutes(2)),
            new FixedTimeProvider(InitialTime.AddMinutes(2)));
        var message = new RuntimeCellDeletionRequestedEvent(
            deleting.Id,
            deleting.DesiredGeneration)
        {
            EventCore = EventCore.Create(deleting.TenantId)
        };

        await consumer.HandleAsync(message);
        await consumer.HandleAsync(message);

        var saved = await DbContext.RuntimeCells.Find(item => item.Id == deleting.Id).SingleAsync();
        Assert.IsType<DeletedRuntimeCellState>(saved.State);
        Assert.Equal(deleting.DesiredGeneration, saved.ObservedGeneration);
        Assert.Equal(1, manager.DeleteCallCount);
        Assert.Equal(1, admission.UnregisterCallCount);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("runtime-cell-deleted"));
    }

    [Fact]
    public async Task DeletionPending_KeepsTargetReferenceAndPersistsRetryableFailure()
    {
        var provisioning = await InsertCellAsync();
        var deleting = provisioning.RequestDeletion(InitialTime.AddMinutes(1));
        await DbContext.RuntimeCells.ReplaceOneAsync(
            cell => cell.Id == provisioning.Id,
            deleting);
        var admission = new RecordingReferenceAdmission();
        var manager = new RecordingNamespaceManager(
            Result.Success(),
            Failures.ExternalService(
                "The runtime cell Kubernetes namespace is still being deleted.",
                "Kubernetes",
                "Deletion is pending."));
        var consumer = new DeleteRuntimeCellOnDeletionRequestedConsumer(
            DbContext,
            manager,
            admission,
            CreateDeletionFinalizer(InitialTime.AddMinutes(2)),
            new FixedTimeProvider(InitialTime.AddMinutes(2)));
        var message = new RuntimeCellDeletionRequestedEvent(
            deleting.Id,
            deleting.DesiredGeneration)
        {
            EventCore = EventCore.Create(deleting.TenantId)
        };

        await consumer.HandleAsync(message);

        var saved = await DbContext.RuntimeCells
            .Find(item => item.Id == deleting.Id)
            .SingleAsync();
        var failed = Assert.IsType<FailedRuntimeCellState>(saved.State);
        Assert.Equal(RuntimeCellOperation.Deletion, failed.Operation);
        Assert.Equal(0, admission.UnregisterCallCount);
        Assert.Equal(0L, await DbContext.CountOutboxMessagesAsync("runtime-cell-deleted"));
    }

    [Fact]
    public async Task DeletionSuccess_FinalizesDeletingResourceGroupAfterLastCell()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(
            TestData.SeedResourceGroup());
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
                new RuntimeCellFactory.CreateOptions(
                    ObjectId.GenerateNewId().ToString(),
                    requestedAt))
            .RequestDeletion(requestedAt.AddMinutes(1));
        await DbContext.RuntimeCells.InsertOneAsync(cell);
        var completedAt = requestedAt.AddMinutes(2);
        var consumer = new DeleteRuntimeCellOnDeletionRequestedConsumer(
            DbContext,
            new RecordingNamespaceManager(Result.Success(), Result.Success()),
            new RecordingReferenceAdmission(),
            CreateDeletionFinalizer(completedAt),
            new FixedTimeProvider(completedAt));
        var message = new RuntimeCellDeletionRequestedEvent(
            cell.Id,
            cell.DesiredGeneration)
        {
            EventCore = EventCore.Create(cell.TenantId)
        };

        await consumer.HandleAsync(message);

        Assert.False(await DbContext.ResourceGroups
            .Find(group => group.Id == resourceGroup.Id)
            .AnyAsync());
        Assert.False(await DbContext.IsolationScopes
            .Find(scope => scope.ResourceGroupId == resourceGroup.Id)
            .AnyAsync());
        Assert.False(await DbContext.RuntimeCells
            .Find(item => item.ResourceGroupId == resourceGroup.Id)
            .AnyAsync());
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("runtime-cell-deleted"));
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("resource-group-deleted"));
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
        var job = new RuntimeCellRecoveryJob(
            DbContext,
            CreateDeletionFinalizer(InitialTime.AddMinutes(2)),
            new FixedTimeProvider(InitialTime.AddMinutes(2)));

        await job.RecoverAsync();

        var recovered = await DbContext.RuntimeCells.Find(item => item.Id == cell.Id).SingleAsync();
        Assert.IsType<ProvisioningRuntimeCellState>(recovered.State);
        Assert.Equal(cell.DesiredGeneration + 1, recovered.DesiredGeneration);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync(
            "runtime-cell-reconciliation-requested"));
        var pendingRequest = await DbContext.OutboxMessages
            .Find(message =>
                message.EventType == "runtime-cell-reconciliation-requested")
            .SingleAsync();
        Assert.Equal(
            RuntimeCellPendingKey.Reconciliation(
                recovered.Id,
                recovered.DesiredGeneration),
            pendingRequest.PendingKey);
        Assert.IsType<PendingOutboxMessageState>(pendingRequest.State);

        await DbContext.OutboxMessages.ReplaceOneAsync(
            message => message.Id == pendingRequest.Id,
            pendingRequest with { PayloadJson = "{ malformed payload" });

        var laterJob = new RuntimeCellRecoveryJob(
            DbContext,
            CreateDeletionFinalizer(InitialTime.AddMinutes(3)),
            new FixedTimeProvider(InitialTime.AddMinutes(3)));
        await laterJob.RecoverAsync();

        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync(
            "runtime-cell-reconciliation-requested"));
    }

    [Fact]
    public async Task Recovery_FinalizesDeletionAfterDeletedCellCrashWindow()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(
            TestData.SeedResourceGroup());
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
                new RuntimeCellFactory.CreateOptions(
                    ObjectId.GenerateNewId().ToString(),
                    requestedAt))
            .RequestDeletion(requestedAt.AddMinutes(1));
        var deleted = deleting.MarkDeleted(
            deleting.DesiredGeneration,
            requestedAt.AddMinutes(2));
        await DbContext.RuntimeCells.InsertOneAsync(deleted);
        var recoveryTime = requestedAt.AddMinutes(3);
        var job = new RuntimeCellRecoveryJob(
            DbContext,
            CreateDeletionFinalizer(recoveryTime),
            new FixedTimeProvider(recoveryTime));

        await job.RecoverAsync();

        Assert.False(await DbContext.ResourceGroups
            .Find(group => group.Id == resourceGroup.Id)
            .AnyAsync());
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("resource-group-deleted"));
    }

    private async Task<RuntimeCell> InsertCellAsync()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(
            TestData.SeedResourceGroup());
        var cell = RuntimeCellFactory.Create(
            resourceGroup.TenantId,
            resourceGroup.Id,
            resourceGroup.DefaultIsolationScopeId,
            "507f1f77bcf86cd799439071",
            "pole-runtime-cell",
            new RuntimeCellFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                InitialTime));
        await DbContext.RuntimeCells.InsertOneAsync(cell);
        return cell;
    }

    private ResourceGroupDeletionFinalizer CreateDeletionFinalizer(DateTime utcNow) =>
        new(
            DbContext,
            new NoUnfinishedDeploymentReferences(),
            new FixedTimeProvider(utcNow));

    private static RuntimeCellReconciliationRequestedEvent ReconciliationEvent(RuntimeCell cell) =>
        new(cell.Id, cell.DesiredGeneration)
        {
            EventCore = EventCore.Create(cell.TenantId)
        };

    private sealed class RecordingNamespaceManager(
        Result createResult,
        Result deleteResult)
        : IRuntimeCellNamespaceManager
    {
        public int EnsureCallCount { get; private set; }
        public int DeleteCallCount { get; private set; }

        public Task<Result> EnsureCreatedAsync(
            RuntimeCell runtimeCell,
            CancellationToken cancellationToken = default)
        {
            EnsureCallCount++;
            return Task.FromResult(createResult);
        }

        public Task<Result> DeleteAsync(
            RuntimeCell runtimeCell,
            CancellationToken cancellationToken = default)
        {
            DeleteCallCount++;
            return Task.FromResult(deleteResult);
        }
    }

    private sealed class RecordingReferenceAdmission
        : IDeploymentTargetReferenceAdmission
    {
        public int UnregisterCallCount { get; private set; }

        public Task<Result> RegisterAsync(
            string deploymentTargetId,
            DeploymentTargetReferenceOwner owner,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result>(Result.Success());

        public Task<Result> UnregisterAsync(
            string deploymentTargetId,
            DeploymentTargetReferenceOwner owner,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
        {
            UnregisterCallCount++;
            return Task.FromResult<Result>(Result.Success());
        }

        public Task<Result> ApplyChangesAsync(
            DeploymentTargetReferenceOwner owner,
            IReadOnlyCollection<string> addedDeploymentTargetIds,
            IReadOnlyCollection<string> removedDeploymentTargetIds,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result>(Result.Success());
    }

    private sealed class NoUnfinishedDeploymentReferences
        : IResourceDeploymentReferenceLookup
    {
        public Task<Result> EnsureNoUnfinishedForResourceGroupAsync(
            string tenantId,
            string resourceGroupId,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result>(Result.Success());

        public Task<Result> EnsureNoUnfinishedForIsolationScopeAsync(
            string tenantId,
            string resourceGroupId,
            string isolationScopeId,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result>(Result.Success());
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
