using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.ResourceConnections.Domain;
using Pole.Workloads;
using Xunit;

namespace Pole.Features.ResourceConnections.Tests;

public sealed class ResourceConnectionTests
{
    private static readonly DateTime Timestamp = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Create_CopiesResolvedPlacementsAndKeepsImmutableReferences()
    {
        var sources = new List<ConnectionSourcePlacement>
        {
            new("source-deployment-1", "source-cell-1", "source-namespace-1")
        };
        var targets = new List<ConnectionTargetSnapshot>
        {
            Target("target-1", "destination-deployment-1", sources)
        };

        var connection = Create(targets);
        sources.Add(new ConnectionSourcePlacement(
            "source-deployment-2", "source-cell-2", "source-namespace-2"));
        targets.Add(Target("target-2", "destination-deployment-2"));

        var active = Assert.IsType<ActiveResourceConnection>(connection.Lifecycle);
        var snapshot = Assert.Single(active.Targets).Snapshot;
        Assert.Single(snapshot.Sources);
        Assert.Equal("source-cell-1", snapshot.Sources[0].RuntimeCellId);
        Assert.Equal("source-namespace-1", snapshot.Sources[0].Namespace);
        Assert.Equal("destination-cell-target-1", snapshot.DestinationRuntimeCellId);
        Assert.Equal("destination-namespace-target-1", snapshot.DestinationNamespace);
        Assert.Equal("source-resource-id", connection.SourceResourceId);
        Assert.Equal("destination-resource-id", connection.DestinationResourceId);
        Assert.Equal("redis", connection.EndpointId);
    }

    [Fact]
    public void Create_RequiresUniqueResolvedSourceAndTargetPlacements()
    {
        var sameTarget = Target("target-1", "destination-deployment-1");
        Assert.Throws<ArgumentException>(() => Create([sameTarget, sameTarget]));
        Assert.Throws<ArgumentException>(() => Create([
            sameTarget,
            Target("target-2", "destination-deployment-2") with
            {
                Sources = sameTarget.Sources
            }
        ]));
        Assert.Throws<ArgumentException>(() => Create([]));
        Assert.Throws<ArgumentException>(() => Target(
            "target-3",
            "destination-deployment-3",
            [
                new("source-deployment", "source-cell-1", "source-namespace-1"),
                new(" source-deployment ", "source-cell-2", "source-namespace-2")
            ]));
    }

    [Fact]
    public void Refresh_FencesOlderSuccessAndFailure_WithoutChangingEndpointIdentity()
    {
        var original = Create([Target("target-1", "destination-deployment-1", port: 6379)]);
        var refreshed = original.RefreshResolvedTargets(
            [Target("target-1", "destination-deployment-1", port: 6380)]);

        Assert.Equal("redis", refreshed.EndpointId);
        Assert.Equal(original.Generation + 1, refreshed.Generation);
        AssertApply<StaleConnectionApply>(refreshed.RecordApplied(
            "target-1", original.Generation, Timestamp));
        AssertApply<StaleConnectionApply>(refreshed.RecordApplyFailure(
            "target-1", original.Generation, "unavailable", Timestamp));
        var active = Assert.IsType<ActiveResourceConnection>(refreshed.Lifecycle);
        Assert.IsType<PendingConnectionApply>(Assert.Single(active.Targets).Status);
        Assert.Equal(6380, active.Targets[0].Snapshot.DestinationPort);
        Assert.Empty(active.RetiringTargets);
    }

    [Fact]
    public void RefreshToNewPolicyLocation_RetainsOldSnapshotThroughResourcePurgeAndDeletion()
    {
        var original = Create([Target("target-1", "old-destination-deployment")]);
        var moved = original.RefreshResolvedTargets([
            Target("target-1", "new-destination-deployment") with
            {
                DestinationRuntimeCellId = "new-destination-cell",
                DestinationNamespace = "new-destination-namespace"
            }
        ]);
        var active = Assert.IsType<ActiveResourceConnection>(moved.Lifecycle);
        var retiring = Assert.Single(active.RetiringTargets);
        Assert.IsType<PendingConnectionCleanup>(retiring.Status);
        Assert.Equal("old-destination-deployment", retiring.Snapshot.DestinationResourceDeploymentId);
        Assert.Equal("destination-namespace-target-1", retiring.Snapshot.DestinationNamespace);
        Assert.Equal("source-deployment-target-1", retiring.Snapshot.Sources[0].ResourceDeploymentId);
        Assert.Equal("source-cell-target-1", retiring.Snapshot.Sources[0].RuntimeCellId);

        // Reloading without either resource aggregate models independent cleanup
        // after the resource records and their placement sets have been purged.
        var reloaded = BsonSerializer.Deserialize<ResourceConnection>(moved.ToBson());
        var deleting = reloaded.BeginDeletion(Timestamp);
        var cleanup = Assert.IsType<DeletingResourceConnection>(deleting.Lifecycle);
        Assert.Equal(2, cleanup.Targets.Count);
        Assert.Contains(cleanup.Targets, target =>
            target.Snapshot.PolicyLocation == Location("target-1") &&
            target.Snapshot.DestinationResourceDeploymentId == "old-destination-deployment");
        Assert.Contains(cleanup.Targets, target =>
            target.Snapshot.DestinationNamespace == "new-destination-namespace" &&
            target.Snapshot.DestinationResourceDeploymentId == "new-destination-deployment");

        var oldCleaned = AssertCleanup<ConnectionCleanupRecorded>(deleting.RecordCleanupCompleted(
            Location("target-1"), deleting.Generation, Timestamp)).Connection;
        Assert.False(oldCleaned.IsCleanupComplete);
        var newCleaned = AssertCleanup<ConnectionCleanupRecorded>(oldCleaned.RecordCleanupCompleted(
            new ConnectionPolicyLocation(
                "target-1", "new-destination-cell", "new-destination-namespace"),
            oldCleaned.Generation,
            Timestamp)).Connection;
        Assert.True(newCleaned.IsCleanupComplete);
    }

    [Fact]
    public void ReintroducedPolicyLocation_CancelsItsRetirementAndFencesOldCleanup()
    {
        var originalSnapshot = Target("target-1", "old-destination-deployment");
        var original = Create([originalSnapshot]);
        var moved = original.RefreshResolvedTargets([
            originalSnapshot with
            {
                DestinationResourceDeploymentId = "new-destination-deployment",
                DestinationRuntimeCellId = "new-destination-cell",
                DestinationNamespace = "new-destination-namespace"
            }
        ]);
        var reintroduced = moved.RefreshResolvedTargets([originalSnapshot]);
        var active = Assert.IsType<ActiveResourceConnection>(reintroduced.Lifecycle);

        Assert.DoesNotContain(active.RetiringTargets, target =>
            target.Snapshot.PolicyLocation == originalSnapshot.PolicyLocation);
        Assert.Single(active.RetiringTargets);
        AssertCleanup<StaleConnectionCleanup>(reintroduced.RecordCleanupCompleted(
            originalSnapshot.PolicyLocation, moved.Generation, Timestamp));
        AssertCleanup<CurrentConnectionPolicyLocation>(reintroduced.RecordCleanupCompleted(
            originalSnapshot.PolicyLocation, reintroduced.Generation, Timestamp));
    }

    [Fact]
    public void RetiringPolicyLocation_CanCompleteWhileConnectionRemainsActive()
    {
        var original = Create([Target("target-1", "old-destination-deployment")]);
        var moved = original.RefreshResolvedTargets([
            Target("target-1", "new-destination-deployment") with
            {
                DestinationRuntimeCellId = "new-destination-cell",
                DestinationNamespace = "new-destination-namespace"
            }
        ]);

        var completed = AssertCleanup<ConnectionCleanupRecorded>(moved.RecordCleanupCompleted(
            Location("target-1"), moved.Generation, Timestamp)).Connection;
        var active = Assert.IsType<ActiveResourceConnection>(completed.Lifecycle);
        Assert.IsType<CompletedConnectionCleanup>(active.RetiringTargets[0].Status);
        var deleting = completed.BeginDeletion(Timestamp);
        Assert.Equal(2, Assert.IsType<DeletingResourceConnection>(deleting.Lifecycle).Targets.Count);
        Assert.Contains(Assert.IsType<DeletingResourceConnection>(deleting.Lifecycle).Targets,
            target => target.Snapshot.PolicyLocation == Location("target-1"));
    }

    [Fact]
    public void RemovingOneTarget_RetainsItsPolicyLocationForLaterDeletion()
    {
        var original = Create([
            Target("target-1", "destination-deployment-1"),
            Target("target-2", "destination-deployment-2")
        ]);
        var refreshed = original.RefreshResolvedTargets([
            Target("target-2", "destination-deployment-2")
        ]);
        var active = Assert.IsType<ActiveResourceConnection>(refreshed.Lifecycle);
        Assert.Equal("target-1", Assert.Single(active.RetiringTargets).Snapshot.TargetId);

        var reloaded = BsonSerializer.Deserialize<ResourceConnection>(refreshed.ToBson());
        var deleting = reloaded.BeginDeletion(Timestamp);
        var cleanup = Assert.IsType<DeletingResourceConnection>(deleting.Lifecycle);
        Assert.Contains(cleanup.Targets, target =>
            target.Snapshot.PolicyLocation == Location("target-1"));
        Assert.Contains(cleanup.Targets, target =>
            target.Snapshot.PolicyLocation == Location("target-2"));
    }

    [Fact]
    public void AppliedTarget_CannotBeDowngradedByALateFailure()
    {
        var connection = Create([Target("target-1", "destination-deployment-1")]);
        var applied = AssertApply<ConnectionApplyRecorded>(connection.RecordApplied(
            "target-1", connection.Generation, Timestamp)).Connection;

        AssertApply<ConnectionApplyAlreadyApplied>(applied.RecordApplyFailure(
            "target-1", applied.Generation, "late failure", Timestamp));
        var active = Assert.IsType<ActiveResourceConnection>(applied.Lifecycle);
        Assert.IsType<AppliedConnectionApply>(active.Targets[0].Status);
    }

    [Fact]
    public void Deletion_PreservesBothPlacementIdentitiesAndCannotReactivate()
    {
        var connection = Create([Target("target-1", "destination-deployment-1")]);
        var deleting = connection.BeginDeletion(Timestamp);

        Assert.Equal("source-resource-id", deleting.SourceResourceId);
        Assert.Equal("destination-resource-id", deleting.DestinationResourceId);
        Assert.Equal("redis", deleting.EndpointId);
        Assert.Equal(connection.Generation + 1, deleting.Generation);
        Assert.Same(deleting, deleting.BeginDeletion(Timestamp));
        AssertApply<StaleConnectionApply>(deleting.RecordApplied(
            "target-1", connection.Generation, Timestamp));
        AssertApply<ConnectionNoLongerActive>(deleting.RecordApplied(
            "target-1", deleting.Generation, Timestamp));
        Assert.Throws<InvalidOperationException>(() => deleting.RefreshResolvedTargets(
            [Target("target-1", "replacement-deployment")]));

        var lifecycle = Assert.IsType<DeletingResourceConnection>(deleting.Lifecycle);
        var snapshot = Assert.Single(lifecycle.Targets).Snapshot;
        Assert.Equal("source-deployment-target-1", snapshot.Sources[0].ResourceDeploymentId);
        Assert.Equal("source-cell-target-1", snapshot.Sources[0].RuntimeCellId);
        Assert.Equal("source-namespace-target-1", snapshot.Sources[0].Namespace);
        Assert.Equal("destination-deployment-1", snapshot.DestinationResourceDeploymentId);
        Assert.Equal("destination-cell-target-1", snapshot.DestinationRuntimeCellId);
        Assert.Equal("destination-namespace-target-1", snapshot.DestinationNamespace);
    }

    [Fact]
    public void Cleanup_RemainsDeletingUntilEveryTargetCompletes()
    {
        var connection = Create([
            Target("target-1", "destination-deployment-1"),
            Target("target-2", "destination-deployment-2")
        ]).BeginDeletion(Timestamp);

        var oneComplete = AssertCleanup<ConnectionCleanupRecorded>(
            connection.RecordCleanupCompleted(Location("target-1"), connection.Generation, Timestamp)).Connection;
        Assert.False(oneComplete.IsCleanupComplete);
        Assert.IsType<DeletingResourceConnection>(oneComplete.Lifecycle);
        var oneFailed = AssertCleanup<ConnectionCleanupRecorded>(
            oneComplete.RecordCleanupFailure(
                Location("target-2"), oneComplete.Generation, "target unavailable", Timestamp)).Connection;
        Assert.False(oneFailed.IsCleanupComplete);
        AssertCleanup<ConnectionCleanupAlreadyCompleted>(oneFailed.RecordCleanupFailure(
            Location("target-1"), oneFailed.Generation, "late failure", Timestamp));
        var allComplete = AssertCleanup<ConnectionCleanupRecorded>(
            oneFailed.RecordCleanupCompleted(Location("target-2"), oneFailed.Generation, Timestamp)).Connection;
        Assert.True(allComplete.IsCleanupComplete);
        Assert.IsType<DeletingResourceConnection>(allComplete.Lifecycle);
        AssertCleanup<StaleConnectionCleanup>(allComplete.RecordCleanupFailure(
            Location("target-2"), allComplete.Generation - 1, "old", Timestamp));
    }

    [Fact]
    public void ApplyFailureIsRetryableWithinTheSameDesiredGeneration()
    {
        var connection = Create([Target("target-1", "destination-deployment-1")]);
        var failed = AssertApply<ConnectionApplyRecorded>(connection.RecordApplyFailure(
            "target-1", connection.Generation, "target unavailable", Timestamp)).Connection;
        var active = Assert.IsType<ActiveResourceConnection>(failed.Lifecycle);
        Assert.IsType<FailedConnectionApply>(active.Targets[0].Status);

        var recovered = AssertApply<ConnectionApplyRecorded>(failed.RecordApplied(
            "target-1", failed.Generation, Timestamp)).Connection;
        Assert.IsType<AppliedConnectionApply>(
            Assert.IsType<ActiveResourceConnection>(recovered.Lifecycle).Targets[0].Status);
    }

    [Fact]
    public void MongoRoundTrip_PreservesClosedApplyAndCleanupStates()
    {
        var connection = Create([Target("target-1", "destination-deployment-1")]);
        var failed = AssertApply<ConnectionApplyRecorded>(connection.RecordApplyFailure(
            "target-1", connection.Generation, "target unavailable", Timestamp)).Connection;
        var reloadedActive = BsonSerializer.Deserialize<ResourceConnection>(failed.ToBson());
        var active = Assert.IsType<ActiveResourceConnection>(reloadedActive.Lifecycle);
        Assert.IsType<FailedConnectionApply>(active.Targets[0].Status);

        var deleting = reloadedActive.BeginDeletion(Timestamp);
        var cleaned = AssertCleanup<ConnectionCleanupRecorded>(deleting.RecordCleanupCompleted(
            Location("target-1"), deleting.Generation, Timestamp)).Connection;
        var reloadedDeleting = BsonSerializer.Deserialize<ResourceConnection>(cleaned.ToBson());
        Assert.True(reloadedDeleting.IsCleanupComplete);
        var cleanup = Assert.IsType<DeletingResourceConnection>(reloadedDeleting.Lifecycle);
        Assert.IsType<CompletedConnectionCleanup>(cleanup.Targets[0].Status);
        Assert.Equal("source-namespace-target-1", cleanup.Targets[0].Snapshot.Sources[0].Namespace);
    }

    private static T AssertApply<T>(ConnectionApplyOutcome outcome)
        where T : class =>
        Assert.IsType<T>(outcome.Value);

    private static T AssertCleanup<T>(ConnectionCleanupOutcome outcome)
        where T : class =>
        Assert.IsType<T>(outcome.Value);

    private static ResourceConnection Create(
        IEnumerable<ConnectionTargetSnapshot> targets) =>
        ResourceConnectionFactory.Create(
            ObjectId.GenerateNewId().ToString(),
            "tenant-id",
            "cache-link",
            ResourceTypeId.Create("pole/web-service"),
            "source-resource-id",
            ResourceTypeId.Create("pole/redis"),
            "destination-resource-id",
            "redis",
            targets);

    private static ConnectionTargetSnapshot Target(
        string targetId,
        string destinationDeploymentId,
        IEnumerable<ConnectionSourcePlacement>? sources = null,
        int port = 6379) =>
        ConnectionTargetSnapshot.Create(
            targetId,
            destinationDeploymentId,
            $"destination-cell-{targetId}",
            $"destination-namespace-{targetId}",
            "redis-destination-service",
            6379,
            port,
            sources ?? [new ConnectionSourcePlacement(
                $"source-deployment-{targetId}",
                $"source-cell-{targetId}",
                $"source-namespace-{targetId}")]);

    private static ConnectionPolicyLocation Location(string targetId) => new(
        targetId,
        $"destination-cell-{targetId}",
        $"destination-namespace-{targetId}");
}
