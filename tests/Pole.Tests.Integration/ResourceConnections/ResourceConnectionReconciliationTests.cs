using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceConnections.Application;
using Pole.Features.ResourceConnections.Application.Jobs;
using Pole.Features.ResourceConnections.Application.Kubernetes;
using Pole.Features.ResourceConnections.Application.Persistence;
using Pole.Features.ResourceConnections.Application.Reconciliation;
using Pole.Features.ResourceConnections.Domain;
using Pole.Tests.Integration.Infrastructure;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.ResourceConnections;

[Collection(IntegrationTestCollection.Name)]
public sealed class ResourceConnectionReconciliationTests(MongoReplicaSetFixture fixture)
    : IAsyncLifetime
{
    private ServiceProvider services = null!;
    private IServiceScope scope = null!;
    private readonly FakePolicyTarget policy = new();
    private IMongoCollection<ResourceConnection> Connections => fixture.Database
        .GetCollection<ResourceConnection>("ResourceConnections");
    private IMongoCollection<ResourceConnectionCleanupTombstone> Tombstones => fixture.Database
        .GetCollection<ResourceConnectionCleanupTombstone>(
            "ResourceConnectionCleanupTombstones");

    public async Task InitializeAsync()
    {
        await fixture.ResetDatabaseAsync();
        var collection = new ServiceCollection();
        collection.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        collection.AddSingleton(fixture.Client);
        collection.AddSingleton(fixture.Database);
        collection.AddSingleton(TimeProvider.System);
        collection.AddSingleton<MongoRunTransaction>();
        collection.AddSingleton<StoreOutboxWrites>(fixture.StoreOutboxWrites);
        collection.AddResourceConnectionsFeature();
        collection.RemoveAll<IResourceConnectionPolicyTarget>();
        collection.AddSingleton<IResourceConnectionPolicyTarget>(policy);
        services = collection.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true
        });
        scope = services.CreateScope();
    }

    public async Task DisposeAsync()
    {
        scope.Dispose();
        await services.DisposeAsync();
    }

    [Fact]
    public async Task FailureIsPerTarget_RecoveryAppliesAndReverifiesDrift()
    {
        var connection = ConnectionWithTwoTargets();
        await Connections.InsertOneAsync(connection);
        var reconciler = scope.ServiceProvider.GetRequiredService<ResourceConnectionReconciler>();
        policy.ApplyFailureTarget = "target-2";

        await reconciler.ReconcileAsync(connection.TenantId, connection.Id, connection.Generation);

        var partial = await LoadAsync(connection.Id);
        var active = Assert.IsType<ActiveResourceConnection>(partial.Lifecycle);
        Assert.IsType<AppliedConnectionApply>(active.Targets[0].Status);
        Assert.Equal("TargetUnavailable", Assert.IsType<FailedConnectionApply>(
            active.Targets[1].Status).Reason);

        policy.ApplyFailureTarget = null;
        await scope.ServiceProvider.GetRequiredService<ResourceConnectionRecoveryJob>()
            .RecoverAsync();
        var applied = Assert.IsType<ActiveResourceConnection>(
            (await LoadAsync(connection.Id)).Lifecycle);
        Assert.All(applied.Targets, target => Assert.IsType<AppliedConnectionApply>(target.Status));
        Assert.True(policy.ApplyAttempts["target-1"] >= 2);

        policy.ApplyFailureTarget = "target-1";
        await scope.ServiceProvider.GetRequiredService<ResourceConnectionRecoveryJob>()
            .RecoverAsync();
        var drift = Assert.IsType<ActiveResourceConnection>(
            (await LoadAsync(connection.Id)).Lifecycle);
        Assert.Equal("TargetUnavailable", Assert.IsType<FailedConnectionApply>(
            drift.Targets[0].Status).Reason);
    }

    [Fact]
    public async Task DeleteRetriesFailedTargetThenPermanentlyRetainsHiddenCleanupAuthority()
    {
        var connection = ConnectionWithTwoTargets();
        await Connections.InsertOneAsync(connection);
        var deletion = await scope.ServiceProvider.GetRequiredService<BeginResourceConnectionDeletion>()(
            connection.TenantId,
            connection.Id);
        var deleting = Assert.IsType<ResourceConnection>(deletion.Value);
        policy.CleanupFailureTarget = "target-2";

        await scope.ServiceProvider.GetRequiredService<ResourceConnectionReconciler>()
            .ReconcileAsync(deleting.TenantId, deleting.Id, deleting.Generation);

        var incomplete = await LoadAsync(connection.Id);
        var cleanup = Assert.IsType<DeletingResourceConnection>(incomplete.Lifecycle);
        Assert.IsType<CompletedConnectionCleanup>(cleanup.Targets[0].Status);
        Assert.Equal("TargetUnavailable", Assert.IsType<FailedConnectionCleanup>(
            cleanup.Targets[1].Status).Reason);
        Assert.Equal(0, await Tombstones.CountDocumentsAsync(item => item.Id == connection.Id));

        policy.CleanupFailureTarget = "target-1";
        await scope.ServiceProvider.GetRequiredService<ResourceConnectionRecoveryJob>()
            .RecoverAsync();
        var rechecked = Assert.IsType<DeletingResourceConnection>(
            (await LoadAsync(connection.Id)).Lifecycle);
        Assert.Equal("TargetUnavailable", Assert.IsType<FailedConnectionCleanup>(
            rechecked.Targets[0].Status).Reason);
        Assert.IsType<CompletedConnectionCleanup>(rechecked.Targets[1].Status);
        Assert.Equal(0, await Tombstones.CountDocumentsAsync(item => item.Id == connection.Id));

        policy.CleanupFailureTarget = null;
        await scope.ServiceProvider.GetRequiredService<ResourceConnectionRecoveryJob>()
            .RecoverAsync();

        Assert.Equal(0, await Connections.CountDocumentsAsync(item => item.Id == connection.Id));
        var tombstone = await Tombstones.Find(item => item.Id == connection.Id).SingleAsync();
        Assert.Equal(connection.TenantId, tombstone.TenantId);
        Assert.Equal(2, tombstone.Targets.Count);
        Assert.Equal("source-namespace-target-1", tombstone.Targets[0].Sources[0].Namespace);
        Assert.Equal(2, policy.TombstoneCleanupAttempts);
        await scope.ServiceProvider.GetRequiredService<ResourceConnectionRecoveryJob>()
            .RecoverAsync();
        Assert.Equal(4, policy.TombstoneCleanupAttempts);
    }

    [Fact]
    public async Task RecoveryFailure_DowngradesCompletedRetiredLocationForDiagnosis()
    {
        var original = ConnectionWithTwoTargets();
        var oldSnapshot = Assert.IsType<ActiveResourceConnection>(original.Lifecycle).Targets[0].Snapshot;
        var moved = original.RefreshResolvedTargets([
            oldSnapshot with
            {
                DestinationRuntimeCellId = "new-destination-cell",
                DestinationNamespace = "new-destination-namespace"
            },
            Target("target-2")
        ]);
        var completed = Assert.IsType<ConnectionCleanupRecorded>(moved.RecordCleanupCompleted(
            oldSnapshot.PolicyLocation,
            moved.Generation,
            DateTime.UtcNow).Value).Connection;
        await Connections.InsertOneAsync(completed);
        policy.CleanupFailureTarget = "target-1";

        await scope.ServiceProvider.GetRequiredService<ResourceConnectionRecoveryJob>()
            .RecoverAsync();

        var active = Assert.IsType<ActiveResourceConnection>(
            (await LoadAsync(completed.Id)).Lifecycle);
        var failed = Assert.Single(active.RetiringTargets);
        Assert.Equal("TargetUnavailable", Assert.IsType<FailedConnectionCleanup>(
            failed.Status).Reason);
    }

    private Task<ResourceConnection> LoadAsync(string id) =>
        Connections.Find(item => item.Id == id).SingleAsync();

    private static ResourceConnection ConnectionWithTwoTargets() =>
        ResourceConnectionFactory.Create(
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            "caller-to-cache",
            ResourceTypeId.Create("pole/web-service"),
            ObjectId.GenerateNewId().ToString(),
            ResourceTypeId.Create("pole/redis"),
            ObjectId.GenerateNewId().ToString(),
            "redis",
            [Target("target-1"), Target("target-2")]);

    private static ConnectionTargetSnapshot Target(string targetId) =>
        ConnectionTargetSnapshot.Create(
            targetId,
            $"destination-deployment-{targetId}",
            $"destination-cell-{targetId}",
            $"destination-namespace-{targetId}",
            "cache-svc",
            6379,
            6379,
            [new ConnectionSourcePlacement(
                $"source-deployment-{targetId}",
                $"source-cell-{targetId}",
                $"source-namespace-{targetId}")]);

    private sealed class FakePolicyTarget : IResourceConnectionPolicyTarget
    {
        public string? ApplyFailureTarget;
        public string? CleanupFailureTarget;
        public readonly Dictionary<string, int> ApplyAttempts = new();
        public int TombstoneCleanupAttempts;

        public Task<ResourceConnectionPolicyTargetOutcome> ApplyAsync(
            ResourceConnection connection,
            ConnectionTargetSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            ApplyAttempts[snapshot.TargetId] =
                ApplyAttempts.GetValueOrDefault(snapshot.TargetId) + 1;
            return Task.FromResult<ResourceConnectionPolicyTargetOutcome>(
                ApplyFailureTarget == snapshot.TargetId
                    ? new ResourceConnectionPolicyFailed(
                        ResourceConnectionPolicyFailureReason.TargetUnavailable)
                    : new ResourceConnectionPolicySucceeded());
        }

        public Task<ResourceConnectionPolicyTargetOutcome> DeleteAsync(
            ResourceConnection connection,
            ConnectionTargetSnapshot snapshot,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ResourceConnectionPolicyTargetOutcome>(
                CleanupFailureTarget == snapshot.TargetId
                    ? new ResourceConnectionPolicyFailed(
                        ResourceConnectionPolicyFailureReason.TargetUnavailable)
                    : new ResourceConnectionPolicySucceeded());

        public Task<ResourceConnectionPolicyTargetOutcome> DeleteTombstoneAsync(
            ResourceConnectionCleanupTombstone tombstone,
            ConnectionTargetSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            TombstoneCleanupAttempts++;
            return Task.FromResult<ResourceConnectionPolicyTargetOutcome>(
                new ResourceConnectionPolicySucceeded());
        }
    }
}
