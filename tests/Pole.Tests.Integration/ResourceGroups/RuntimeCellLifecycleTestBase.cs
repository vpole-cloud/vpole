// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceGroups.Application.Jobs;
using Pole.Features.ResourceGroups.Application.Kubernetes;
using Pole.Features.ResourceGroups.Application.ResourceGroups;
using Pole.Features.ResourceGroups.Application.RuntimeCells;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Events;
using Pole.Tests.Integration.Infrastructure;

namespace Pole.Tests.Integration.ResourceGroups;

public abstract class RuntimeCellLifecycleTestBase(
    MongoReplicaSetFixture fixture,
    bool isolatedDatabase = false
) : MediatorIntegrationTestBase(fixture, isolatedDatabase)
{
    private protected static readonly DateTime InitialTime = new(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);

    private protected async Task<RuntimeCell> InsertCellAsync()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
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

    private protected RuntimeCellLifecyclePersistence CreateLifecyclePersistence(
        DateTime utcNow,
        ApplyDeploymentTargetReferenceChanges? applyReferenceChanges = null) => new(
        DbContext.RuntimeCells,
        StoreOutboxWrites,
        applyReferenceChanges ?? NoOpReferenceChanges,
        new MongoRunTransaction(Fixture.Client).InvokeOutcomeAsync,
        new MongoRunTransaction(Fixture.Client).InvokeOutcomeAsync,
        new FixedTimeProvider(utcNow));

    private protected RuntimeCellRecoveryJob CreateRecoveryJob(DateTime utcNow)
    {
        var transaction = new MongoRunTransaction(Fixture.Client);
        var persistence = new RuntimeCellRecoveryPersistence(
            DbContext.IsolationScopes,
            DbContext.RuntimeCells,
            StoreOutboxWrites,
            transaction.InvokeOutcomeAsync);
        return new RuntimeCellRecoveryJob(persistence, CreateDeletionFinalizer(utcNow), new FixedTimeProvider(utcNow));
    }

    private protected FinalizeResourceGroupDeletion CreateDeletionFinalizer(DateTime utcNow) =>
        Services.GetRequiredService<FinalizeResourceGroupDeletion>();

    private protected Task StoreOutboxWrites(
        IClientSessionHandle session,
        IReadOnlyCollection<OutboxWrite> writes,
        CancellationToken cancellationToken = default) => Fixture.StoreOutboxWrites(session, writes, cancellationToken);

    private protected static Task<ApplyDeploymentTargetReferenceChangesOutcome> NoOpReferenceChanges(
        ApplyDeploymentTargetReferenceChangesRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ApplyDeploymentTargetReferenceChangesOutcome>(new DeploymentTargetReferencesChanged());

    private protected static RuntimeCellReconciliationRequestedEvent ReconciliationEvent(RuntimeCell cell) =>
        new(cell.Id, cell.DesiredGeneration)
        {
            EventCore = EventCore.Create(cell.TenantId),
        };

    private protected static RuntimeCellDeletionRequestedEvent DeletionEvent(RuntimeCell cell) =>
        new(cell.Id, cell.DesiredGeneration, null, 0)
        {
            EventCore = EventCore.Create(cell.TenantId),
        };

    private protected sealed class RecordingNamespaceManager(
        RuntimeCellEnsureOutcome ensureResult,
        RuntimeCellDeleteOutcome deleteResult
    )
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

    private protected sealed class RecordingReferenceAdmission
    {
        public int UnregisterCallCount { get; private set; }

        public Task<ApplyDeploymentTargetReferenceChangesOutcome> ApplyChangesOutcomeAsync(
            ApplyDeploymentTargetReferenceChangesRequest request,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
        {
            UnregisterCallCount += request.RemovedDeploymentTargetIds.Count;
            return Task.FromResult<ApplyDeploymentTargetReferenceChangesOutcome>(
                new DeploymentTargetReferencesChanged());
        }
    }

    private protected sealed class FixedTimeProvider(
        DateTime utcNow
    ) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
