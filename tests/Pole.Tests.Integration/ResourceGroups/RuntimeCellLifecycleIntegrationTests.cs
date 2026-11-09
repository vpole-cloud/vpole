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

[Collection(IntegrationTestCollection.Tenant3)]
public sealed class RuntimeCellLifecycleIntegrationTests(MongoReplicaSetFixture fixture)
    : RuntimeCellLifecycleTestBase(fixture)
{
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
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync(Data.TenantId, "pole.resource-groups/v1/runtime-cell-ready"));
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
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync(Data.TenantId,
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
        Assert.Equal(0L, await DbContext.CountOutboxMessagesAsync(Data.TenantId,
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
        var consumer = new RuntimeCellDeletionProgress(
            CreateLifecyclePersistence(InitialTime.AddMinutes(2), admission.ApplyChangesOutcomeAsync),
            manager.DeleteAsync,
            CreateDeletionFinalizer(InitialTime.AddMinutes(2)),
            new FixedTimeProvider(InitialTime.AddMinutes(2)));

        await consumer.HandleAsync(DeletionEvent(deleting));
        await consumer.HandleAsync(DeletionEvent(deleting));

        var saved = await DbContext.RuntimeCells.Find(item => item.Id == deleting.Id).SingleAsync();
        Assert.IsType<DeletedRuntimeCellState>(saved.State);
        Assert.Equal(deleting.DesiredGeneration, saved.ObservedGeneration);
        Assert.Equal(1, manager.DeleteCallCount);
        Assert.Equal(1, admission.UnregisterCallCount);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync(Data.TenantId, "pole.resource-groups/v1/runtime-cell-deleted"));
    }

    [Fact]
    public async Task DeletionPending_KeepsTargetReferenceAndSchedulesProgressWithoutFailure()
    {
        var provisioning = await InsertCellAsync();
        var deleting = provisioning.RequestDeletion(InitialTime.AddMinutes(1));
        await DbContext.RuntimeCells.ReplaceOneAsync(cell => cell.Id == provisioning.Id, deleting);
        var admission = new RecordingReferenceAdmission();
        var manager = new RecordingNamespaceManager(
            Unit.Value,
            new RuntimeCellNamespaceDeletionPending("pole-runtime-cell"));
        var consumer = new RuntimeCellDeletionProgress(
            CreateLifecyclePersistence(InitialTime.AddMinutes(2), admission.ApplyChangesOutcomeAsync),
            manager.DeleteAsync,
            CreateDeletionFinalizer(InitialTime.AddMinutes(2)),
            new FixedTimeProvider(InitialTime.AddMinutes(2)));

        await consumer.HandleAsync(DeletionEvent(deleting));

        var saved = await DbContext.RuntimeCells.Find(item => item.Id == deleting.Id).SingleAsync();
        var pending = Assert.IsType<DeletingRuntimeCellState>(saved.State);
        Assert.Equal(deleting.DesiredGeneration, saved.DesiredGeneration);
        Assert.Equal(1, pending.CheckSequence);
        Assert.Equal(InitialTime.AddMinutes(2).AddSeconds(5), pending.NextCheckAtUtc);
        Assert.Equal(0, admission.UnregisterCallCount);
        Assert.Equal(0L, await DbContext.CountOutboxMessagesAsync(Data.TenantId, "pole.resource-groups/v1/runtime-cell-deleted"));
    }

    [Fact]
    public async Task DeletionSuccess_FinalizesDeletingResourceGroupAfterLastCell()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
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
        var consumer = new RuntimeCellDeletionProgress(
            CreateLifecyclePersistence(
                completedAt,
                new RecordingReferenceAdmission().ApplyChangesOutcomeAsync),
            new RecordingNamespaceManager(Unit.Value, Unit.Value).DeleteAsync,
            CreateDeletionFinalizer(completedAt),
            new FixedTimeProvider(completedAt));

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
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync(Data.TenantId, "pole.resource-groups/v1/runtime-cell-deleted"));
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync(Data.TenantId, "pole.resource-groups/v1/resource-group-deleted"));
    }
}

[Collection(IntegrationTestCollection.Global)]
public sealed class RuntimeCellRecoveryIntegrationTests(MongoReplicaSetFixture fixture)
    : RuntimeCellLifecycleTestBase(fixture, true), IIsolatedIntegrationTest
{
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
        Assert.Equal(1L, await DbContext.CountAllOutboxMessagesAsync(
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

        Assert.Equal(1L, await DbContext.CountAllOutboxMessagesAsync(
            "pole.resource-groups/v1/runtime-cell-reconciliation-requested"));
    }

    [Fact]
    public async Task Recovery_FinalizesDeletionAfterDeletedCellCrashWindow()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
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
        Assert.Equal(1L, await DbContext.CountAllOutboxMessagesAsync("pole.resource-groups/v1/resource-group-deleted"));
    }
}
