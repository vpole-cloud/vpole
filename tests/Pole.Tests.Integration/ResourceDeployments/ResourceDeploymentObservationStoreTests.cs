using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Pole.Database;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Application.Persistence.Admission;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Observation;
using Pole.Features.ResourceDeployments.Application.Persistence.Observation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceDeployments.Events;
using Pole.Observability;
using Pole.Tests.Integration.Observation;
using Pole.Tests.Integration.Infrastructure;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

[Collection(IntegrationTestCollection.Name)]
public sealed class ResourceDeploymentObservationStoreTests(MongoReplicaSetFixture fixture) : IAsyncLifetime
{
    private const string EventName = "pole.resource-deployments/v1/resource-deployment-observation-changed";

    private IMongoCollection<ObservationControlDocument> Controls =>
        fixture.Database.GetCollection<ObservationControlDocument>(MongoResourceDeploymentObservationStore
            .ControlsCollection);

    private IMongoCollection<ObservationDemandDocument> Demands =>
        fixture.Database.GetCollection<ObservationDemandDocument>(MongoResourceDeploymentObservationStore
            .DemandsCollection);

    private IMongoCollection<ObservationSnapshotDocument> Snapshots =>
        fixture.Database.GetCollection<ObservationSnapshotDocument>(MongoResourceDeploymentObservationStore
            .SnapshotsCollection);

    private IMongoCollection<BsonDocument> Targets => fixture.Database.GetCollection<BsonDocument>("DeploymentTargets");
    public Task InitializeAsync() => fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task DemandAndSourceDollarPrefixedValuesAreStoredAsLiterals()
    {
        var seeded = await SeedAsync();
        var demand = seeded.Demand with
        {
            Identity = seeded.Demand.Identity with
            {
                SubscriptionId = "$interest",
                ConnectionId = "$$NOW",
                SessionId = "$SessionId"
            }
        };
        var store = Store();
        Assert.IsType<ObservationMutationAccepted>((await store.PutDemandAsync(demand, seeded.Context)).Value);
        var persisted = await Demands.Find(item => item.SubscriptionId == "$interest").SingleAsync();
        Assert.Equal("$$NOW", persisted.ConnectionId);
        Assert.Equal("$SessionId", persisted.SessionId);
        var owner = Assert.IsType<ObservationOwnership>((await store.ClaimLeaseAsync(seeded.Context, "replica")).Value);
        const string binding = "$ObservationVersion";
        var source = Assert.IsType<ObservationSourceState>((await store.SetSourceStateAsync(owner.Lease,
            seeded.Context, owner.SourceControlVersion, true, binding)).Value);
        Assert.IsType<ResourceDeploymentSharedObservation>((await store.TryCommitSnapshotAsync(owner.Lease,
            seeded.Context, source.SourceControlVersion, binding, Sample() with { BindingId = binding })).Value);
        var control = await store.LoadControlAsync(new(owner.Lease.TenantId, owner.Lease.DeploymentId));
        Assert.Equal(binding, control!.BindingId);
        Assert.NotNull(await store.LoadFreshSnapshotAsync(seeded.Context));
    }

    [Fact]
    public async Task HistoricalRawDocumentsRemainReadableRenewableAndWritableWithoutMigration()
    {
        var seeded = await SeedAsync();
        var context = seeded.Context;
        var now = await ServerNowAsync();
        var (_, control) = ObservationDocumentCompatibilityTests.Legacy("runtime-control");
        var (_, demand) = ObservationDocumentCompatibilityTests.Legacy("runtime-demand");
        var (_, snapshot) = ObservationDocumentCompatibilityTests.Legacy("runtime-snapshot");
        foreach (var document in new[] { control, snapshot })
        {
            document["_id"] = ObjectId.Parse(context.Deployment.Id);
            document["TenantId"] = ObjectId.Parse(context.Deployment.TenantId);
            document["Pin"] = ObservationValueMappings.Pin(context).ToBsonDocument();
            document["BindingId"] = "owned-controller";
        }
        control["LeaseExpiresAtUtc"] = now.AddSeconds(30);
        control["UpdatedAtUtc"] = now;
        snapshot["CheckedAtUtc"] = now;
        demand["_id"] = $"{context.Deployment.TenantId}/{context.Deployment.Id}/interest";
        demand["TenantId"] = ObjectId.Parse(context.Deployment.TenantId);
        demand["DeploymentId"] = ObjectId.Parse(context.Deployment.Id);
        demand["OriginalExpiresAtUtc"] = now.AddMinutes(5);
        demand["ExpiresAtUtc"] = now.AddMinutes(1);
        await fixture.Database.GetCollection<BsonDocument>(MongoResourceDeploymentObservationStore.ControlsCollection)
            .InsertOneAsync(control);
        await fixture.Database.GetCollection<BsonDocument>(MongoResourceDeploymentObservationStore.DemandsCollection)
            .InsertOneAsync(demand);
        await fixture.Database.GetCollection<BsonDocument>(MongoResourceDeploymentObservationStore.SnapshotsCollection)
            .InsertOneAsync(snapshot);

        var store = Store();
        var loaded = await store.LoadControlAsync(new(context.Deployment.TenantId, context.Deployment.Id));
        Assert.Equal(8, loaded!.ObservationVersion);
        Assert.Equal(8, (await store.LoadFreshSnapshotAsync(context))!.ObservationVersion);
        var lease = new ObservationLease(context.Deployment.TenantId, context.Deployment.Id, "replica", "lease-token", 6);
        Assert.IsType<ObservationMutationAccepted>((await store.RenewLeaseAsync(lease, context)).Value);
        Assert.IsType<ObservationMutationAccepted>((await store.RenewDemandAsync(
            new(new(context.Deployment.TenantId, context.Deployment.Id, "interest", "connection", "session"),
                now.AddMinutes(10)), context)).Value);
        var committed = Assert.IsType<ResourceDeploymentSharedObservation>((await store.TryCommitSnapshotAsync(
            lease, context, 7, "owned-controller", Sample())).Value);
        Assert.Equal(9, committed.ObservationVersion);
        var written = await fixture.Database
            .GetCollection<BsonDocument>(MongoResourceDeploymentObservationStore.SnapshotsCollection)
            .Find(new BsonDocument("_id", ObjectId.Parse(context.Deployment.Id))).SingleAsync();
        Assert.Equal(snapshot.Names, written.Names);
        Assert.Equal(snapshot["Pin"], written["Pin"]);
        Assert.Equal(9, written["ObservationVersion"].AsInt64);
    }

    [Fact]
    public async Task Representative_LeaseSourceAndSnapshotCommitTogetherWithMinimalTenantOutbox()
    {
        var seeded = await SeedAsync();
        var store = Store();
        Assert.IsType<ObservationMutationAccepted>((await store.PutDemandAsync(seeded.Demand, seeded.Context)).Value);
        var owner = Assert.IsType<ObservationOwnership>(
            (await store.ClaimLeaseAsync(seeded.Context, "replica-a")).Value);
        Assert.Null(await store.LoadFreshSnapshotAsync(seeded.Context));
        var source = Assert.IsType<ObservationSourceState>((await store.SetSourceStateAsync(owner.Lease,
            seeded.Context,
            owner.SourceControlVersion,
            true,
            "owned-controller")).Value);
        using var trace =
            TraceContext.Push(TraceContext.Create("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "observation-store"));
        var snapshot = Assert.IsType<ResourceDeploymentSharedObservation>((await store.TryCommitSnapshotAsync(
            owner.Lease,
            seeded.Context,
            source.SourceControlVersion,
            "owned-controller",
            Sample())).Value);
        Assert.Equal(2, snapshot.ObservationVersion);
        Assert.IsType<ObservedWorkload>(snapshot.Observation);
        Assert.IsType<AvailableWorkloadDiagnostics>(snapshot.Diagnostics);
        var shared = await Store().LoadFreshSnapshotAsync(seeded.Context);
        Assert.Equal(snapshot.ObservationVersion, shared!.ObservationVersion);
        Assert.Equal(snapshot.CheckedAtUtc, shared.CheckedAtUtc);
        var envelope = (await fixture.DbContext.OutboxMessages.Find(item => item.EventType == EventName).ToListAsync())
            .Single(item => item.CorrelationId == "observation-store");
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", envelope.TraceId);
        Assert.Equal("observation-store", envelope.CorrelationId);
        var message =
            JsonSerializer.Deserialize<ResourceDeploymentObservationChangedEvent>(envelope.PayloadJson,
                EventExtensions.SerializerOptions)!;
        Assert.Equal(seeded.Context.Deployment.TenantId, message.EventCore.TenantId);
        Assert.Equal(seeded.Context.Deployment.Id, message.DeploymentId);
        Assert.Equal(seeded.Context.Deployment.ResourceId, message.ResourceId);
        Assert.Equal(seeded.Context.Deployment.ResourceGroupId, message.ResourceGroupId);
        Assert.Equal(2, message.ObservationVersion);
        Assert.DoesNotContain("owned-controller", envelope.PayloadJson);
        Assert.DoesNotContain("pod-uid", envelope.PayloadJson);
        Assert.DoesNotContain("Containers", envelope.PayloadJson);
    }

    [Fact]
    public async Task Representative_OutboxFailureRollsBackSnapshotCountersAndBothDocumentFences()
    {
        var seeded = await SeedAsync();
        var owner = await StartAsync(seeded);
        var before = await Controls.Find(item => item.Id == seeded.Context.Deployment.Id).SingleAsync();
        var deploymentBefore = await fixture.DbContext.ResourceDeployments
            .Find(item => item.Id == seeded.Context.Deployment.Id).SingleAsync();
        var targetBefore = await Targets.Find(TargetId(seeded.Context)).SingleAsync();
        var failing = Store(async (session, writes, ct) =>
        {
            await fixture.StoreOutboxWrites(session, writes, ct);
            throw new InvalidOperationException("Injected observation outbox failure.");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.TryCommitSnapshotAsync(owner.Lease,
            seeded.Context,
            owner.SourceControlVersion,
            "owned-controller",
            Sample()));
        Assert.Equal(before, await Controls.Find(item => item.Id == before.Id).SingleAsync());
        Assert.Equal(deploymentBefore.ReferenceFence,
            (await fixture.DbContext.ResourceDeployments.Find(item => item.Id == before.Id).SingleAsync())
            .ReferenceFence);
        Assert.Equal(targetBefore["ObservationFence"],
            (await Targets.Find(TargetId(seeded.Context)).SingleAsync())["ObservationFence"]);
        Assert.Empty(await Snapshots.Find(FilterDefinition<ObservationSnapshotDocument>.Empty).ToListAsync());
        Assert.Equal(1,
            await fixture.DbContext.OutboxMessages.CountDocumentsAsync(item => item.EventType == EventName));
    }

    [Fact]
    public async Task Representative_TwoIndependentStoresHaveOneOwnerAndRenewWithoutSourceChurn()
    {
        var seeded = await SeedAsync();
        Assert.IsType<ObservationMutationAccepted>((await Store().PutDemandAsync(seeded.Demand, seeded.Context)).Value);
        var claims = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(index => Store().ClaimLeaseAsync(seeded.Context, $"replica-{index}")));
        var owner = Assert.Single(claims.Select(claim => claim.Value).OfType<ObservationOwnership>());
        Assert.Equal(7,
            claims.Count(claim => claim.Value is ObservationStoreRejected { Reason: ObservationStoreRejection.Busy }));
        var source = Assert.IsType<ObservationSourceState>((await Store().SetSourceStateAsync(owner.Lease,
            seeded.Context,
            owner.SourceControlVersion,
            true,
            "owned-controller")).Value);
        Assert.IsType<ObservationMutationAccepted>((await Store().RenewLeaseAsync(owner.Lease, seeded.Context)).Value);
        Assert.IsType<ObservationMutationAccepted>(
            (await Store().RenewDemandAsync(seeded.Demand, seeded.Context)).Value);
        var unchanged = Assert.IsType<ObservationSourceState>((await Store().SetSourceStateAsync(owner.Lease,
            seeded.Context,
            source.SourceControlVersion,
            true,
            "owned-controller")).Value);
        Assert.Equal(source.SourceControlVersion, unchanged.SourceControlVersion);
        var control =
            await Store().LoadControlAsync(new(seeded.Context.Deployment.TenantId, seeded.Context.Deployment.Id));
        Assert.Equal(owner.Lease.Epoch, control!.Epoch);
        Assert.Equal(source.SourceControlVersion, control.SourceControlVersion);
        Assert.Equal(1, control.ObservationVersion);
        Assert.Equal(1,
            await fixture.DbContext.OutboxMessages.CountDocumentsAsync(item => item.EventType == EventName));
    }

    [Theory]
    [InlineData("source-loss")]
    [InlineData("release")]
    public async Task Representative_NegativeTransitionCommitsHintWhenTargetDisabledAndOutboxFailureRollsItBack(
        string transition)
    {
        var seeded = await SeedAsync();
        var owner = await StartAsync(seeded);
        await Store().TryCommitSnapshotAsync(owner.Lease,
            seeded.Context,
            owner.SourceControlVersion,
            "owned-controller",
            Sample());
        var before = await Controls.Find(item => item.Id == seeded.Context.Deployment.Id).SingleAsync();
        await Targets.UpdateOneAsync(TargetId(seeded.Context), Builders<BsonDocument>.Update.Set("Enabled", false));
        var failing = Store(async (session, writes, ct) =>
        {
            await fixture.StoreOutboxWrites(session, writes, ct);
            throw new InvalidOperationException("Injected negative-transition outbox failure.");
        });

        Task Mutate(MongoResourceDeploymentObservationStore store) => transition == "source-loss"
            ? store.SetSourceStateAsync(owner.Lease, seeded.Context, owner.SourceControlVersion, false, null)
            : store.ReleaseLeaseAsync(owner.Lease);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Mutate(failing));
        Assert.Equal(before, await Controls.Find(item => item.Id == before.Id).SingleAsync());
        Assert.Equal(2,
            await fixture.DbContext.OutboxMessages.CountDocumentsAsync(item => item.EventType == EventName));
        await Mutate(Store());
        var after = await Controls.Find(item => item.Id == before.Id).SingleAsync();
        Assert.False(after.SourceHealthy);
        Assert.Null(after.BindingId);
        Assert.Equal(3, after.ObservationVersion);
        Assert.True(after.SourceControlVersion > before.SourceControlVersion);
        Assert.Null(await Store().LoadFreshSnapshotAsync(seeded.Context));
        if (transition == "source-loss")
            await Store().SetSourceStateAsync(owner.Lease, seeded.Context, after.SourceControlVersion, false, null);
        else
            AssertRejected(ObservationStoreRejection.LeaseLost, (await Store().ReleaseLeaseAsync(owner.Lease)).Value);
        var repeated = await Controls.Find(item => item.Id == before.Id).SingleAsync();
        Assert.Equal(after.ObservationVersion, repeated.ObservationVersion);
        Assert.Equal(after.Epoch, repeated.Epoch);
        Assert.Equal(3,
            await fixture.DbContext.OutboxMessages.CountDocumentsAsync(item => item.EventType == EventName));
        var messages = await fixture.DbContext.OutboxMessages.Find(item => item.EventType == EventName).ToListAsync();
        var notice = messages.Select(item =>
                JsonSerializer.Deserialize<ResourceDeploymentObservationChangedEvent>(item.PayloadJson,
                    EventExtensions.SerializerOptions)!)
            .Single(item => item.ObservationVersion == 3);
        Assert.Equal(seeded.Context.Deployment.TenantId, notice.EventCore.TenantId);
        Assert.Equal(seeded.Context.Deployment.ResourceId, notice.ResourceId);
        Assert.All(messages, item => Assert.DoesNotContain("owned-controller", item.PayloadJson));
    }

    [Fact]
    public async Task Representative_NoDemandOrUnhealthySourceNeverPersistsSnapshot()
    {
        var seeded = await SeedAsync();
        AssertRejected(ObservationStoreRejection.NoDemand,
            (await Store().ClaimLeaseAsync(seeded.Context, "replica-a")).Value);
        await Store().PutDemandAsync(seeded.Demand, seeded.Context);
        var owner = Assert.IsType<ObservationOwnership>((await Store().ClaimLeaseAsync(seeded.Context, "replica-a"))
            .Value);
        AssertRejected(ObservationStoreRejection.BindingChanged,
            (await Store().TryCommitSnapshotAsync(owner.Lease,
                seeded.Context,
                owner.SourceControlVersion,
                "owned-controller",
                Sample())).Value);
        Assert.Null(await Store().LoadFreshSnapshotAsync(seeded.Context));
        Assert.Equal(0, await Snapshots.CountDocumentsAsync(FilterDefinition<ObservationSnapshotDocument>.Empty));
        Assert.Equal(0,
            await fixture.DbContext.OutboxMessages.CountDocumentsAsync(item => item.EventType == EventName));
    }

    [Fact]
    public async Task ExpiredLease_TakeoverNotifiesAndFencesLateOldOwnerSamplesRenewalsAndReleases()
    {
        var seeded = await SeedAsync();
        var old = await StartAsync(seeded);
        var snapshot = Assert.IsType<ResourceDeploymentSharedObservation>((await Store().TryCommitSnapshotAsync(
            old.Lease,
            seeded.Context,
            old.SourceControlVersion,
            "owned-controller",
            Sample())).Value);
        await Controls.UpdateOneAsync(item => item.Id == old.Lease.DeploymentId,
            Builders<ObservationControlDocument>.Update.Set(item => item.LeaseExpiresAtUtc, DateTime.UnixEpoch));
        var next = Assert.IsType<ObservationOwnership>((await Store().ClaimLeaseAsync(seeded.Context, "replica-b"))
            .Value);
        Assert.True(next.Lease.Epoch > old.Lease.Epoch);
        var control = await Store().LoadControlAsync(new(next.Lease.TenantId, next.Lease.DeploymentId));
        Assert.False(control!.SourceHealthy);
        Assert.Equal(snapshot.ObservationVersion + 1, control.ObservationVersion);
        AssertRejected(ObservationStoreRejection.LeaseLost,
            (await Store().RenewLeaseAsync(old.Lease, seeded.Context)).Value);
        AssertRejected(ObservationStoreRejection.LeaseLost, (await Store().ReleaseLeaseAsync(old.Lease)).Value);
        AssertRejected(ObservationStoreRejection.LeaseLost,
            (await Store().TryCommitSnapshotAsync(old.Lease,
                seeded.Context,
                old.SourceControlVersion,
                "owned-controller",
                Sample())).Value);
        AssertRejected(ObservationStoreRejection.LeaseLost,
            (await Store().SetSourceStateAsync(old.Lease,
                seeded.Context,
                old.SourceControlVersion,
                false,
                null)).Value);
        Assert.Equal(control, await Store().LoadControlAsync(new(next.Lease.TenantId, next.Lease.DeploymentId)));
        Assert.Equal(3, await EventCountAsync());
        Assert.Equal(0, (await Targets.Find(TargetId(seeded.Context)).SingleAsync())["ReferenceFence"].AsInt64);
    }

    [Fact]
    public async Task FinalExactDemandRemovalFencesOwnerButOtherInterestAndWrongIdentityDoNot()
    {
        var seeded = await SeedAsync();
        var owner = await StartAsync(seeded);
        await Store().TryCommitSnapshotAsync(owner.Lease,
            seeded.Context,
            owner.SourceControlVersion,
            "owned-controller",
            Sample());
        var second = seeded.Demand with
        {
            Identity = seeded.Demand.Identity with
            {
                SubscriptionId = "second",
                ConnectionId = "connection-b",
                SessionId = "session-b"
            }
        };
        await Store().PutDemandAsync(second, seeded.Context);
        await Store().RemoveDemandAsync(seeded.Demand.Identity with
        {
            SessionId = "unrelated-session"
        });
        Assert.Equal(2, await Demands.CountDocumentsAsync(FilterDefinition<ObservationDemandDocument>.Empty));
        await Store().RemoveDemandAsync(seeded.Demand.Identity);
        var retained = await Store().LoadControlAsync(new(owner.Lease.TenantId, owner.Lease.DeploymentId));
        Assert.Equal(owner.Lease.Epoch, retained!.Epoch);
        Assert.True(retained.SourceHealthy);
        var serverBefore = await ServerNowAsync();
        await Store().RemoveDemandAsync(second.Identity);
        var removed = await Store().LoadControlAsync(new(owner.Lease.TenantId, owner.Lease.DeploymentId));
        Assert.True(removed!.Epoch > owner.Lease.Epoch);
        Assert.True(removed.ObservationVersion > retained.ObservationVersion);
        Assert.False(removed.SourceHealthy);
        Assert.Null(removed.LeaseExpiresAtUtc);
        var expiredSnapshot = await Snapshots.Find(item => item.Id == owner.Lease.DeploymentId).SingleAsync();
        Assert.InRange(expiredSnapshot.ExpiresAtUtc!.Value,
            serverBefore.AddSeconds(300),
            (await ServerNowAsync()).AddSeconds(300));
        AssertRejected(ObservationStoreRejection.NoDemand,
            (await Store().TryCommitSnapshotAsync(owner.Lease,
                seeded.Context,
                owner.SourceControlVersion,
                "owned-controller",
                Sample())).Value);
        AssertRejected(ObservationStoreRejection.LeaseLost, (await Store().ReleaseLeaseAsync(owner.Lease)).Value);
        await Store().RemoveDemandAsync(second.Identity);
        await Store().ReapExpiredDemandsAsync(32);
        Assert.Equal(removed, await Store().LoadControlAsync(new(owner.Lease.TenantId, owner.Lease.DeploymentId)));
        Assert.Empty((await Store().ScanDemandsAsync(null, 32)).Items);
        Assert.Equal(2, await EventCountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredDemandReapingFencesOwnerEvenWhenTtlHasAlreadyDeletedTheRow(bool ttlAlreadyDeleted)
    {
        var seeded = await SeedAsync();
        var owner = await StartAsync(seeded);
        await Store().TryCommitSnapshotAsync(owner.Lease,
            seeded.Context,
            owner.SourceControlVersion,
            "owned-controller",
            Sample());
        if (ttlAlreadyDeleted)
            await Demands.DeleteManyAsync(FilterDefinition<ObservationDemandDocument>.Empty);
        else
            await Demands.UpdateManyAsync(FilterDefinition<ObservationDemandDocument>.Empty,
                Builders<ObservationDemandDocument>.Update.Set(item => item.ExpiresAtUtc, DateTime.UnixEpoch));
        AssertRejected(ObservationStoreRejection.NoDemand,
            (await Store().RenewLeaseAsync(owner.Lease, seeded.Context)).Value);
        AssertRejected(ObservationStoreRejection.NoDemand,
            (await Store().TryCommitSnapshotAsync(owner.Lease,
                seeded.Context,
                owner.SourceControlVersion,
                "owned-controller",
                Sample())).Value);
        await Store().ReapExpiredDemandsAsync(32);
        var control = await Controls.Find(item => item.Id == owner.Lease.DeploymentId).SingleAsync();
        Assert.True(control.Epoch > owner.Lease.Epoch);
        Assert.False(control.SourceHealthy);
        Assert.NotNull(control.IdleSinceUtc);
        Assert.Null(control.Token);
        Assert.NotNull((await Snapshots.Find(item => item.Id == owner.Lease.DeploymentId).SingleAsync()).ExpiresAtUtc);
        Assert.Equal(0, await Demands.CountDocumentsAsync(FilterDefinition<ObservationDemandDocument>.Empty));
        Assert.IsType<ObservationMutationAccepted>(
            (await Store().RenewDemandAsync(seeded.Demand, seeded.Context)).Value);
        var next = Assert.IsType<ObservationOwnership>((await Store().ClaimLeaseAsync(seeded.Context, "replica-b"))
            .Value);
        Assert.True(next.Lease.Epoch > owner.Lease.Epoch);
        AssertRejected(ObservationStoreRejection.LeaseLost, (await Store().ReleaseLeaseAsync(owner.Lease)).Value);
    }

    [Fact]
    public async Task FinalUnsubscribeDuringSampleTransactionRejectsTheLateCommitOnRetry()
    {
        var seeded = await SeedAsync();
        var owner = await StartAsync(seeded);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 0;
        var paused = Store(fence: async (request, session, ct) =>
        {
            var result = await FenceTargetAsync(request, session, ct);
            if (Interlocked.Exchange(ref first, 1) == 0)
            {
                entered.SetResult();
                await resume.Task.WaitAsync(ct);
            }

            return result;
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = paused.TryCommitSnapshotAsync(owner.Lease,
            seeded.Context,
            owner.SourceControlVersion,
            "owned-controller",
            Sample(),
            timeout.Token);
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            await Store().RemoveDemandAsync(seeded.Demand.Identity, timeout.Token);
        }
        finally
        {
            resume.TrySetResult();
        }

        AssertRejected(ObservationStoreRejection.NoDemand, (await pending).Value);
        Assert.Equal(0, await Snapshots.CountDocumentsAsync(FilterDefinition<ObservationSnapshotDocument>.Empty));
        Assert.Equal(1, await EventCountAsync());
        Assert.False((await Store().LoadControlAsync(new(owner.Lease.TenantId, owner.Lease.DeploymentId)))!
            .SourceHealthy);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("generation")]
    [InlineData("resource-revision")]
    [InlineData("resource")]
    [InlineData("group")]
    [InlineData("isolation")]
    [InlineData("runtime-cell")]
    [InlineData("target")]
    [InlineData("namespace")]
    [InlineData("target-configuration")]
    [InlineData("fixed-replicas")]
    [InlineData("compute")]
    [InlineData("purge")]
    public async Task EveryExactDeploymentFenceRejectsAStaleSampleAndCachedSnapshot(string mutation)
    {
        var seeded = await SeedAsync();
        var owner = await StartAsync(seeded);
        await Store().TryCommitSnapshotAsync(owner.Lease,
            seeded.Context,
            owner.SourceControlVersion,
            "owned-controller",
            Sample());
        if (mutation == "purge")
            await fixture.DbContext.ResourceDeployments.DeleteOneAsync(item => item.Id == owner.Lease.DeploymentId);
        else
        {
            var change = mutation switch
            {
                "revision" => Builders<ResourceDeployment>.Update.Inc(item => item.Revision, 1),
                "generation" => Builders<ResourceDeployment>.Update.Inc(item => item.DesiredGeneration, 1),
                "resource-revision" => Builders<ResourceDeployment>.Update.Inc(item => item.ResourceRevision, 1),
                "resource" => Builders<ResourceDeployment>.Update.Set(item => item.ResourceId,
                    ObjectId.GenerateNewId().ToString()),
                "group" => Builders<ResourceDeployment>.Update.Set(item => item.ResourceGroupId,
                    ObjectId.GenerateNewId().ToString()),
                "isolation" => Builders<ResourceDeployment>.Update.Set(item => item.IsolationScopeId,
                    ObjectId.GenerateNewId().ToString()),
                "runtime-cell" => Builders<ResourceDeployment>.Update.Set(item => item.RuntimeCellId,
                    ObjectId.GenerateNewId().ToString()),
                "target" => Builders<ResourceDeployment>.Update.Set(item => item.ResolvedPlacement.DeploymentTargetId,
                    ObjectId.GenerateNewId().ToString()),
                "namespace" => Builders<ResourceDeployment>.Update.Set(item =>
                        item.ResolvedPlacement.KubernetesNamespace,
                    "changed-namespace"),
                "target-configuration" => Builders<ResourceDeployment>.Update.Inc(item =>
                        item.ResolvedPlacement.TargetWorkloadConfigurationRevision,
                    1),
                "fixed-replicas" => Builders<ResourceDeployment>.Update.Inc(item => item.FixedReplicas, 1),
                "compute" => Builders<ResourceDeployment>.Update.Inc(item =>
                        item.ResolvedPlacement.Compute.ComputeProfileRevision,
                    1),
                _ => throw new InvalidOperationException("Unknown test mutation.")
            };
            await fixture.DbContext.ResourceDeployments.UpdateOneAsync(item => item.Id == owner.Lease.DeploymentId,
                change);
        }

        var before = await Store().LoadControlAsync(new(owner.Lease.TenantId, owner.Lease.DeploymentId));
        AssertRejected(ObservationStoreRejection.Superseded,
            (await Store().TryCommitSnapshotAsync(owner.Lease,
                seeded.Context,
                owner.SourceControlVersion,
                "owned-controller",
                Sample())).Value);
        Assert.Null(await Store().LoadFreshSnapshotAsync(seeded.Context));
        Assert.Equal(before, await Store().LoadControlAsync(new(owner.Lease.TenantId, owner.Lease.DeploymentId)));
        Assert.Equal(2, await EventCountAsync());
    }

    [Theory]
    [InlineData("rotation")]
    [InlineData("disabled")]
    [InlineData("deletion")]
    [InlineData("missing")]
    public async Task TargetChangesRejectOldSnapshotsButCannotPreventNegativeCoverageFencing(string mutation)
    {
        var seeded = await SeedAsync();
        var owner = await StartAsync(seeded);
        await Store().TryCommitSnapshotAsync(owner.Lease,
            seeded.Context,
            owner.SourceControlVersion,
            "owned-controller",
            Sample());
        if (mutation == "missing")
            await Targets.DeleteOneAsync(TargetId(seeded.Context));
        else
            await Targets.UpdateOneAsync(TargetId(seeded.Context),
                mutation switch
                {
                    "rotation" => Builders<BsonDocument>.Update.Inc("Connection.Version", 1L),
                    "disabled" => Builders<BsonDocument>.Update.Set("Enabled", false),
                    "deletion" => Builders<BsonDocument>.Update.Set("DeletionRequestedAtUtc", DateTime.UtcNow),
                    _ => throw new InvalidOperationException("Unknown target mutation.")
                });
        AssertRejected(ObservationStoreRejection.TargetUnavailable,
            (await Store().TryCommitSnapshotAsync(owner.Lease,
                seeded.Context,
                owner.SourceControlVersion,
                "owned-controller",
                Sample())).Value);
        Assert.Null(await Store().LoadFreshSnapshotAsync(seeded.Context));
        var source = Assert.IsType<ObservationSourceState>((await Store().SetSourceStateAsync(owner.Lease,
            seeded.Context,
            owner.SourceControlVersion,
            false,
            null)).Value);
        Assert.True(source.SourceControlVersion > owner.SourceControlVersion);
        Assert.False((await Store().LoadControlAsync(new(owner.Lease.TenantId, owner.Lease.DeploymentId)))!
            .SourceHealthy);
        Assert.Equal(3, await EventCountAsync());
    }

    [Fact]
    public async Task AcceptedPinChangeFencesOldOwnerAndPublishesOnlyCurrentCommittedContext()
    {
        var seeded = await SeedAsync();
        var owner = await StartAsync(seeded);
        await Store().TryCommitSnapshotAsync(owner.Lease,
            seeded.Context,
            owner.SourceControlVersion,
            "owned-controller",
            Sample());
        var old = await fixture.DbContext.ResourceDeployments.Find(item => item.Id == owner.Lease.DeploymentId)
            .SingleAsync();
        var next = old.RequestReconciliation(old.ResolvedPlacement,
            true,
            DateTime.UtcNow,
            resourceRevision: old.ResourceRevision + 1);
        await fixture.DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == old.Id, next);
        var context = new ResourceDeploymentObservationContext(next.ToReconciliationSnapshot(), next.Revision, 1);
        await Store().PutDemandAsync(seeded.Demand, context);
        var control = await Store().LoadControlAsync(new(owner.Lease.TenantId, owner.Lease.DeploymentId));
        Assert.False(control!.SourceHealthy);
        Assert.True(control.Epoch > owner.Lease.Epoch);
        Assert.Equal(3, control.ObservationVersion);
        AssertRejected(ObservationStoreRejection.LeaseLost, (await Store().ReleaseLeaseAsync(owner.Lease)).Value);
        var notices = await fixture.DbContext.OutboxMessages.Find(item => item.EventType == EventName).ToListAsync();
        var changed = notices.Select(item =>
                JsonSerializer.Deserialize<ResourceDeploymentObservationChangedEvent>(item.PayloadJson,
                    EventExtensions.SerializerOptions)!)
            .Single(item => item.ObservationVersion == 3);
        Assert.Equal(next.Revision, changed.DeploymentRevision);
        Assert.Equal(next.DesiredGeneration, changed.DesiredGeneration);
        Assert.Equal(next.ResourceId, changed.ResourceId);
        Assert.Equal(next.TenantId, changed.EventCore.TenantId);
    }

    [Fact]
    public async Task SnapshotTtlDeletionAndReturningDemandNeverResetRetainedVersions()
    {
        var seeded = await SeedAsync();
        var owner = await StartAsync(seeded);
        var first = Assert.IsType<ResourceDeploymentSharedObservation>((await Store().TryCommitSnapshotAsync(
            owner.Lease,
            seeded.Context,
            owner.SourceControlVersion,
            "owned-controller",
            Sample())).Value);
        await Store().RemoveDemandAsync(seeded.Demand.Identity);
        Assert.NotNull((await Snapshots.Find(item => item.Id == owner.Lease.DeploymentId).SingleAsync()).ExpiresAtUtc);
        await Store().PutDemandAsync(seeded.Demand, seeded.Context);
        Assert.Null((await Snapshots.Find(item => item.Id == owner.Lease.DeploymentId).SingleAsync()).ExpiresAtUtc);
        await Snapshots.DeleteOneAsync(item => item.Id == owner.Lease.DeploymentId);
        var next = Assert.IsType<ObservationOwnership>((await Store().ClaimLeaseAsync(seeded.Context, "replica-b"))
            .Value);
        var source = Assert.IsType<ObservationSourceState>((await Store().SetSourceStateAsync(next.Lease,
            seeded.Context,
            next.SourceControlVersion,
            true,
            "owned-controller")).Value);
        var snapshot = Assert.IsType<ResourceDeploymentSharedObservation>((await Store().TryCommitSnapshotAsync(
            next.Lease,
            seeded.Context,
            source.SourceControlVersion,
            "owned-controller",
            Sample())).Value);
        Assert.True(snapshot.ObservationVersion > first.ObservationVersion);
        Assert.True(snapshot.CheckedAtUtc >= first.CheckedAtUtc);
        Assert.True(next.Lease.Epoch > owner.Lease.Epoch);
        Assert.Single(await Controls.Find(FilterDefinition<ObservationControlDocument>.Empty).ToListAsync());
    }

    [Fact]
    public async Task DemandAndLeaseExpiriesUseMongoClockAndRenewalNeverExtendsOriginalExpiry()
    {
        var seeded = await SeedAsync();
        var before = await ServerNowAsync();
        var farFuture = seeded.Demand with
        {
            ExpiresAtUtc = DateTimeOffset.MaxValue
        };
        await Store().PutDemandAsync(farFuture, seeded.Context);
        var demand = await Demands.Find(FilterDefinition<ObservationDemandDocument>.Empty).SingleAsync();
        Assert.InRange(demand.ExpiresAtUtc, before.AddSeconds(60), (await ServerNowAsync()).AddSeconds(60));
        var shortBound = new DateTimeOffset((await ServerNowAsync()).AddSeconds(10));
        await Store().RenewDemandAsync(farFuture with
        {
            ExpiresAtUtc = shortBound
        }, seeded.Context);
        await Store().RenewDemandAsync(farFuture, seeded.Context);
        demand = await Demands.Find(FilterDefinition<ObservationDemandDocument>.Empty).SingleAsync();
        Assert.InRange(Math.Abs((demand.OriginalExpiresAtUtc - shortBound.UtcDateTime).TotalMilliseconds), 0, 1);
        Assert.True(demand.ExpiresAtUtc <= demand.OriginalExpiresAtUtc);
        before = await ServerNowAsync();
        var owner = Assert.IsType<ObservationOwnership>((await Store().ClaimLeaseAsync(seeded.Context, "replica-a"))
            .Value);
        var control = await Store().LoadControlAsync(new(owner.Lease.TenantId, owner.Lease.DeploymentId));
        Assert.InRange(control!.LeaseExpiresAtUtc!.Value,
            before.AddSeconds(30),
            (await ServerNowAsync()).AddSeconds(30));
        var past = seeded.Demand with
        {
            Identity = seeded.Demand.Identity with
            {
                SubscriptionId = "past-clock"
            },
            ExpiresAtUtc = DateTimeOffset.UnixEpoch
        };
        AssertRejected(ObservationStoreRejection.NoDemand, (await Store().PutDemandAsync(past, seeded.Context)).Value);
        Assert.Equal(1, await Demands.CountDocumentsAsync(FilterDefinition<ObservationDemandDocument>.Empty));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleOrFutureCheckedTimeIsNeverFreshAndUnchangedValidationAdvancesVersion(bool future)
    {
        var seeded = await SeedAsync();
        var owner = await StartAsync(seeded);
        var first = Assert.IsType<ResourceDeploymentSharedObservation>((await Store().TryCommitSnapshotAsync(
            owner.Lease,
            seeded.Context,
            owner.SourceControlVersion,
            "owned-controller",
            Sample())).Value);
        var before = await Snapshots.Find(item => item.Id == owner.Lease.DeploymentId).SingleAsync();
        await Snapshots.UpdateOneAsync(item => item.Id == before.Id,
            Builders<ObservationSnapshotDocument>.Update.Set(item => item.CheckedAtUtc,
                future ? DateTime.UnixEpoch.AddDays(50000) : DateTime.UnixEpoch));
        Assert.Null(await Store().LoadFreshSnapshotAsync(seeded.Context));
        var next = Assert.IsType<ResourceDeploymentSharedObservation>((await Store().TryCommitSnapshotAsync(owner.Lease,
            seeded.Context,
            owner.SourceControlVersion,
            "owned-controller",
            Sample())).Value);
        Assert.Equal(first.ObservationVersion + 1, next.ObservationVersion);
        Assert.Equal(before.Fingerprint,
            (await Snapshots.Find(item => item.Id == before.Id).SingleAsync()).Fingerprint);
        Assert.NotNull(await Store().LoadFreshSnapshotAsync(seeded.Context));
    }

    [Fact]
    public async Task SnapshotAndOutboxRemainInvisibleOutsideTheirTransactionUntilCommit()
    {
        var seeded = await SeedAsync();
        var owner = await StartAsync(seeded);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = Store(async (session, writes, ct) =>
        {
            await fixture.StoreOutboxWrites(session, writes, ct);
            entered.TrySetResult();
            await resume.Task.WaitAsync(ct);
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = paused.TryCommitSnapshotAsync(owner.Lease,
            seeded.Context,
            owner.SourceControlVersion,
            "owned-controller",
            Sample(),
            timeout.Token);
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            Assert.Equal(0, await Snapshots.CountDocumentsAsync(FilterDefinition<ObservationSnapshotDocument>.Empty));
            Assert.Equal(1,
                (await Store().LoadControlAsync(new(owner.Lease.TenantId, owner.Lease.DeploymentId)))!
                .ObservationVersion);
            Assert.Equal(1, await EventCountAsync());
        }
        finally
        {
            resume.TrySetResult();
        }

        Assert.IsType<ResourceDeploymentSharedObservation>((await pending).Value);
        Assert.Equal(1, await Snapshots.CountDocumentsAsync(FilterDefinition<ObservationSnapshotDocument>.Empty));
        Assert.Equal(2, await EventCountAsync());
    }

    [Fact]
    public async Task LeaseExpiringDuringOutboxAwaitNeverCommitsLateSnapshot()
    {
        var seeded = await SeedAsync();
        var owner = await StartAsync(seeded);
        await Controls.UpdateOneAsync(item => item.Id == owner.Lease.DeploymentId,
            Builders<ObservationControlDocument>.Update.Set(item => item.LeaseExpiresAtUtc,
                (await ServerNowAsync()).AddSeconds(1)));
        var delayed = Store(async (session, writes, ct) =>
        {
            await fixture.StoreOutboxWrites(session, writes, ct);
            await Task.Delay(TimeSpan.FromMilliseconds(1400), ct);
        });
        AssertRejected(ObservationStoreRejection.LeaseLost,
            (await delayed.TryCommitSnapshotAsync(owner.Lease,
                seeded.Context,
                owner.SourceControlVersion,
                "owned-controller",
                Sample())).Value);
        Assert.Equal(0, await Snapshots.CountDocumentsAsync(FilterDefinition<ObservationSnapshotDocument>.Empty));
        Assert.Equal(1, await EventCountAsync());
    }

    [Fact]
    public async Task DemandExpiringDuringOutboxAwaitNeverCommitsLateSnapshot()
    {
        var seeded = await SeedAsync();
        var owner = await StartAsync(seeded);
        await Demands.UpdateManyAsync(FilterDefinition<ObservationDemandDocument>.Empty,
            Builders<ObservationDemandDocument>.Update.Set(item => item.ExpiresAtUtc,
                (await ServerNowAsync()).AddSeconds(1)));
        var delayed = Store(async (session, writes, ct) =>
        {
            await fixture.StoreOutboxWrites(session, writes, ct);
            await Task.Delay(TimeSpan.FromMilliseconds(1400), ct);
        });
        AssertRejected(ObservationStoreRejection.NoDemand,
            (await delayed.TryCommitSnapshotAsync(owner.Lease,
                seeded.Context,
                owner.SourceControlVersion,
                "owned-controller",
                Sample())).Value);
        Assert.Equal(0, await Snapshots.CountDocumentsAsync(FilterDefinition<ObservationSnapshotDocument>.Empty));
        Assert.Equal(1, await EventCountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnchangedHealthyStateCannotAcknowledgeAfterItsFinalAwaitCrossesExpiry(bool demandExpires)
    {
        var seeded = await SeedAsync();
        var owner = await StartAsync(seeded);
        var expiry = (await ServerNowAsync()).AddSeconds(1);
        if (demandExpires)
            await Demands.UpdateManyAsync(FilterDefinition<ObservationDemandDocument>.Empty,
                Builders<ObservationDemandDocument>.Update.Set(item => item.ExpiresAtUtc, expiry));
        else
            await Controls.UpdateOneAsync(item => item.Id == owner.Lease.DeploymentId,
                Builders<ObservationControlDocument>.Update.Set(item => item.LeaseExpiresAtUtc, expiry));
        var before = await Controls.Find(item => item.Id == owner.Lease.DeploymentId).SingleAsync();
        var settings = fixture.Client.Settings.Clone();
        var previous = settings.ClusterConfigurator;
        var updates = 0;
        var touchRequest = 0;
        settings.ClusterConfigurator = builder =>
        {
            previous?.Invoke(builder);
            builder.Subscribe<CommandStartedEvent>(command =>
            {
                if (command.CommandName == "update" && command.Command["update"] ==
                    MongoResourceDeploymentObservationStore.ControlsCollection)
                    Volatile.Write(ref touchRequest, command.RequestId);
            });
            builder.Subscribe<CommandSucceededEvent>(command =>
            {
                if (command.RequestId == Volatile.Read(ref touchRequest) && Interlocked.Increment(ref updates) == 1)
                    Thread.Sleep(1400);
            });
        };
        using var client = new MongoClient(settings);
        var database = client.GetDatabase(fixture.Database.DatabaseNamespace.DatabaseName);
        var delayed = new MongoResourceDeploymentObservationStore(database,
            new MongoRunTransaction(client),
            (request, session, ct) => new MongoFenceDeploymentTargetObservation(
                    database.GetCollection<Pole.Features.DeploymentTargets.Domain.DeploymentTarget>(
                        "DeploymentTargets"))
                .InvokeAsync(request, session, ct),
            fixture.StoreOutboxWrites);
        AssertRejected(demandExpires ? ObservationStoreRejection.NoDemand : ObservationStoreRejection.LeaseLost,
            (await delayed.SetSourceStateAsync(owner.Lease,
                seeded.Context,
                owner.SourceControlVersion,
                true,
                "owned-controller")).Value);
        Assert.Equal(before.ToBsonDocument(),
            (await Controls.Find(item => item.Id == owner.Lease.DeploymentId).SingleAsync()).ToBsonDocument());
        Assert.Equal(1, await EventCountAsync());
        Assert.True(updates >= 1);
    }

    [Fact]
    public async Task DemandScanIsDistinctCursorBoundedAndTenantFilteredReadsCannotCrossScopes()
    {
        var one = await SeedAsync();
        var two = await SeedAsync();
        var three = await SeedAsync();
        foreach (var seeded in new[] { one, two, three })
        {
            await Store().PutDemandAsync(seeded.Demand, seeded.Context);
            await Store().PutDemandAsync(seeded.Demand with
            {
                Identity = seeded.Demand.Identity with
                {
                    SubscriptionId = "additional"
                }
            },
                seeded.Context);
        }

        var first = await Store().ScanDemandsAsync(null, 2);
        Assert.Equal(2, first.Items.Count);
        Assert.NotNull(first.NextCursor);
        var last = await Store().ScanDemandsAsync(first.NextCursor, 2);
        Assert.Single(last.Items);
        Assert.Null(last.NextCursor);
        Assert.Equal(3, first.Items.Concat(last.Items).Select(item => item.DeploymentId).Distinct().Count());
        Assert.Null(await Store()
            .LoadControlAsync(new(ObjectId.GenerateNewId().ToString(), one.Context.Deployment.Id)));
        var foreign = one.Context with
        {
            Deployment = one.Context.Deployment with
            {
                TenantId = ObjectId.GenerateNewId().ToString()
            }
        };
        Assert.Null(await Store().LoadFreshSnapshotAsync(foreign));
        Assert.Equal(0, await EventCountAsync());
    }

    private Task<long> EventCountAsync() =>
        fixture.DbContext.OutboxMessages.CountDocumentsAsync(item => item.EventType == EventName);

    private async Task<DateTime> ServerNowAsync() =>
        (await fixture.Database.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1)))["localTime"]
        .ToUniversalTime();

    private MongoResourceDeploymentObservationStore Store(StoreOutboxWrites? writes = null,
        FenceDeploymentTargetObservation? fence = null) =>
        new(fixture.Database,
            new MongoRunTransaction(fixture.Client),
            fence ?? FenceTargetAsync,
            writes ?? fixture.StoreOutboxWrites);

    private Task<FenceDeploymentTargetObservationOutcome> FenceTargetAsync(
        DeploymentTargetObservationFenceRequest request,
        IClientSessionHandle session,
        CancellationToken ct) =>
        new MongoFenceDeploymentTargetObservation(fixture.DbContext.DeploymentTargets)
            .InvokeAsync(request, session, ct);

    private async Task<SeededObservation> SeedAsync()
    {
        await ObservationStoreIndexes.CreateAsync(fixture.Database);
        var now = DateTime.UtcNow.AddMinutes(-1);
        var target = ObjectId.GenerateNewId().ToString();
        var deployment = ResourceDeploymentFactory.Create(TestData.TenantId,
            ResourceTypeId.Create("pole/web-service"),
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            ResolvedResourcePlacement.Create(ObjectId.GenerateNewId().ToString(),
                target,
                "pole-observation-test",
                1,
                1,
                null,
                null,
                ResourceDeploymentTestData.Compute(),
                now),
            ResourcePlacementCapability.SingleTarget,
            true,
            new(ObjectId.GenerateNewId().ToString(), now));
        await fixture.DbContext.ResourceDeployments.InsertOneAsync(deployment);
        await Targets.InsertOneAsync(new BsonDocument
        {
            { "_id", ObjectId.Parse(target) }, { "Enabled", true }, { "DeletionRequestedAtUtc", BsonNull.Value },
            { "Connection", new BsonDocument("Version", 1L) }, { "ObservationFence", 0L }, { "ReferenceFence", 0L }
        });
        var context =
            new ResourceDeploymentObservationContext(deployment.ToReconciliationSnapshot(), deployment.Revision, 1);
        return new(context,
            new(new(TestData.TenantId, deployment.Id, Guid.NewGuid().ToString("N"), "connection-a", "session-a"),
                DateTimeOffset.UtcNow.AddMinutes(20)));
    }

    private async Task<ObservationOwnership> StartAsync(SeededObservation seeded)
    {
        Assert.IsType<ObservationMutationAccepted>((await Store().PutDemandAsync(seeded.Demand, seeded.Context)).Value);
        var owner = Assert.IsType<ObservationOwnership>((await Store().ClaimLeaseAsync(seeded.Context, "replica-a"))
            .Value);
        var source = Assert.IsType<ObservationSourceState>((await Store().SetSourceStateAsync(owner.Lease,
            seeded.Context,
            owner.SourceControlVersion,
            true,
            "owned-controller")).Value);
        return owner with
        {
            SourceControlVersion = source.SourceControlVersion
        };
    }

    private static ResourceDeploymentBoundObservation Sample() => new("owned-controller",
        new ObservedWorkload(true,
            true,
            1,
            1,
            1,
            1,
            1,
            false,
            [new("pod-a", "Running", true, true, 0, [])],
            [],
            null))
    {
        Diagnostics = new AvailableWorkloadDiagnostics([
                new("pod-uid", "pod-a", "Running", true, true, 0, [], [new("app", "Application", false)], false)
            ],
            false,
            [],
            false,
            null)
    };

    private static void AssertRejected(ObservationStoreRejection expected, object? actual) =>
        Assert.Equal(expected, Assert.IsType<ObservationStoreRejected>(actual).Reason);

    private static FilterDefinition<BsonDocument> TargetId(ResourceDeploymentObservationContext context) =>
        new BsonDocument("_id", ObjectId.Parse(context.Deployment.Placement.DeploymentTargetId));

    private sealed record SeededObservation(
        ResourceDeploymentObservationContext Context,
        ObservationDemand Demand);
}
