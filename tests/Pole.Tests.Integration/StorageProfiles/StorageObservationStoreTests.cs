using System.Text.Json;
using System.Security.Claims;
using Pole.ASP.Helpers;
using Pole.Features.Realtime.Contracts;
using Pole.Features.StorageProfiles.Application.Realtime;
using System.Collections.Immutable;
using k8s.Models;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Pole.Database;
using Pole.Features.DeploymentTargets.Application.Persistence.Admission;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.StorageProfiles.Application.Observation;
using Pole.Features.StorageProfiles.Application.Persistence.Observation;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.StorageProfiles.Events;
using Pole.Observability;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.StorageProfiles;

[Collection(IntegrationTestCollection.Name)]
public sealed class StorageObservationStoreTests(MongoReplicaSetFixture fixture) : IAsyncLifetime
{
    private const string EventName = "pole.storage-profiles/v1/storage-observation-changed";
    private IMongoCollection<StorageObservationControlDocument> Controls => fixture.Database.GetCollection<StorageObservationControlDocument>(MongoStorageObservationStore.ControlsCollection);
    private IMongoCollection<StorageObservationDemandDocument> Demands => fixture.Database.GetCollection<StorageObservationDemandDocument>(MongoStorageObservationStore.DemandsCollection);
    private IMongoCollection<StorageObservationSnapshotDocument> Snapshots => fixture.Database.GetCollection<StorageObservationSnapshotDocument>(MongoStorageObservationStore.SnapshotsCollection);
    private IMongoCollection<BsonDocument> Targets => fixture.Database.GetCollection<BsonDocument>("DeploymentTargets");
    public Task InitializeAsync() => fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task LeaseTakeoverFencesLateOldOwnerSamplesRenewalsAndReleases()
    {
        var seeded = await SeedAsync(); var old = await StartAsync(seeded);
        await Store().TryCommitSnapshotAsync(old.Lease, seeded.Context, old.SourceControlVersion, "source-a", Inventory());
        await Controls.UpdateOneAsync(item => item.Id == seeded.Context.DeploymentTargetId, Builders<StorageObservationControlDocument>.Update.Set(item => item.LeaseExpiresAtUtc, DateTime.UnixEpoch));
        var next = Assert.IsType<StorageObservationOwnership>((await Store().ClaimLeaseAsync(seeded.Context, "replica-b")).Value);
        Assert.True(next.Lease.Epoch > old.Lease.Epoch); Assert.Equal(3, await EventCountAsync()); Assert.Null(await Store().LoadFreshSnapshotAsync(seeded.Context));
        Rejected(StorageObservationRejection.LeaseLost, (await Store().TryCommitSnapshotAsync(old.Lease, seeded.Context, old.SourceControlVersion, "source-a", Inventory())).Value);
        Rejected(StorageObservationRejection.LeaseLost, (await Store().RenewLeaseAsync(old.Lease, seeded.Context)).Value);
        Rejected(StorageObservationRejection.LeaseLost, (await Store().ReleaseLeaseAsync(old.Lease)).Value);
        var state = Assert.IsType<StorageObservationSourceState>((await Store().SetSourceStateAsync(next.Lease, seeded.Context, next.SourceControlVersion, true, "source-b")).Value);
        Assert.IsType<StorageObservationSnapshot>((await Store().TryCommitSnapshotAsync(next.Lease, seeded.Context, state.SourceControlVersion, "source-b", Inventory())).Value);
        Assert.Equal(5, await EventCountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalDemandRemovalOrTtlReapingFencesBeforeLateSamplesAndRetainsVersions(bool reap)
    {
        var seeded = await SeedAsync(); var owner = await StartAsync(seeded);
        var committed = Assert.IsType<StorageObservationSnapshot>((await Store().TryCommitSnapshotAsync(owner.Lease, seeded.Context, owner.SourceControlVersion, "source-a", Inventory())).Value);
        var other = seeded.Demand with { Identity = seeded.Demand.Identity with { SubscriptionId = "other" } };
        await Store().PutDemandAsync(other, seeded.Context);
        await Store().RemoveDemandAsync(seeded.Demand.Identity with { SessionId = "wrong-session" });
        Assert.Equal(2, await Demands.CountDocumentsAsync(FilterDefinition<StorageObservationDemandDocument>.Empty));
        await Store().RemoveDemandAsync(other.Identity); Assert.NotNull(await Store().LoadFreshSnapshotAsync(seeded.Context));
        var now = await ServerNowAsync();
        if (reap) { await Demands.DeleteManyAsync(FilterDefinition<StorageObservationDemandDocument>.Empty); await Store().ReapExpiredDemandsAsync(10); }
        else await Store().RemoveDemandAsync(seeded.Demand.Identity);
        var control = (await Store().LoadControlAsync(seeded.Context.DeploymentTargetId))!;
        Assert.True(control.Epoch > owner.Lease.Epoch); Assert.False(control.SourceHealthy); Assert.Null(control.LeaseExpiresAtUtc); Assert.True(control.ObservationVersion > committed.ObservationVersion);
        var snapshot = await Snapshots.Find(FilterDefinition<StorageObservationSnapshotDocument>.Empty).SingleAsync();
        Assert.InRange(snapshot.ExpiresAtUtc!.Value, now.AddMinutes(5), (await ServerNowAsync()).AddMinutes(5));
        Rejected(StorageObservationRejection.NoDemand, (await Store().TryCommitSnapshotAsync(owner.Lease, seeded.Context, owner.SourceControlVersion, "source-a", Inventory())).Value);
        await Snapshots.DeleteManyAsync(FilterDefinition<StorageObservationSnapshotDocument>.Empty);
        await Store().PutDemandAsync(seeded.Demand, seeded.Context);
        var returning = Assert.IsType<StorageObservationOwnership>((await Store().ClaimLeaseAsync(seeded.Context, "replica-b")).Value);
        Assert.True(returning.Lease.Epoch > control.Epoch); Assert.Equal(control.ObservationVersion, (await Store().LoadControlAsync(seeded.Context.DeploymentTargetId))!.ObservationVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NegativeSourceOrReleaseOutboxFailureCannotWithdrawCommittedCoverage(bool release)
    {
        var seeded = await SeedAsync(); var owner = await StartAsync(seeded);
        await Store().TryCommitSnapshotAsync(owner.Lease, seeded.Context, owner.SourceControlVersion, "source-a", Inventory());
        var before = await Controls.Find(FilterDefinition<StorageObservationControlDocument>.Empty).SingleAsync();
        var failing = Store(async (session, writes, ct) => { await fixture.StoreOutboxWrites(session, writes, ct); throw new InvalidOperationException("Injected negative outbox failure."); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => release ? (Task)failing.ReleaseLeaseAsync(owner.Lease) : failing.SetSourceStateAsync(owner.Lease, seeded.Context, owner.SourceControlVersion, false, null));
        Assert.Equal(before, await Controls.Find(FilterDefinition<StorageObservationControlDocument>.Empty).SingleAsync()); Assert.Equal(2, await EventCountAsync()); Assert.NotNull(await Store().LoadFreshSnapshotAsync(seeded.Context));
    }

    [Fact]
    public async Task LifecycleUsesExactSessionIdentitiesAndFreshCoverageWithoutExtendingExpiry()
    {
        var seeded = await SeedAsync();
        var loader = new StorageObservationContextLoader((id, _) => Task.FromResult<LookupDeploymentTargetOutcome>(new DeploymentTargetLookupDto(id, "Target", "cluster", "InCluster", 1, new(1, null, new(ImmutableDictionary<string, DeploymentTargetNetworkSourceDto>.Empty)), 1, true, false, false, "Healthy")));
        var lifecycle = new StorageObservationLifecycle(new(), loader, Store(), new());
        var subscription = new RealtimeSubscription(seeded.Demand.Identity.SubscriptionId, seeded.Demand.Identity.ConnectionId, seeded.Demand.Identity.SessionId, new("storage-observations", ResourceId: seeded.Context.DeploymentTargetId), seeded.Demand.ExpiresAtUtc);
        var admin = new ClaimsPrincipal(new ClaimsIdentity([new(Claims.Roles, Roles.Admin)], "test"));
        Assert.False(await lifecycle.SubscribeAsync(subscription, admin, default));
        var original = await Demands.Find(FilterDefinition<StorageObservationDemandDocument>.Empty).SingleAsync();
        var owner = await StartAsync(seeded); await Store().TryCommitSnapshotAsync(owner.Lease, seeded.Context, owner.SourceControlVersion, "source-a", Inventory());
        Assert.True(await lifecycle.RenewAsync(subscription with { ExpiresAtUtc = DateTimeOffset.MaxValue }, admin, default));
        var renewed = await Demands.Find(FilterDefinition<StorageObservationDemandDocument>.Empty).SingleAsync(); Assert.Equal(original.Id, renewed.Id); Assert.Equal(original.OriginalExpiresAtUtc, renewed.OriginalExpiresAtUtc);
        await lifecycle.UnsubscribeAsync(subscription with { ConnectionId = "wrong" }, default); Assert.Equal(1, await Demands.CountDocumentsAsync(FilterDefinition<StorageObservationDemandDocument>.Empty));
        Assert.False(await lifecycle.RenewAsync(subscription, new ClaimsPrincipal(new ClaimsIdentity([], "test")), default));
        Assert.Equal(0, await Demands.CountDocumentsAsync(FilterDefinition<StorageObservationDemandDocument>.Empty)); Assert.False((await Store().LoadControlAsync(seeded.Context.DeploymentTargetId))!.SourceHealthy);
        Assert.False(await lifecycle.SubscribeAsync(subscription with { Request = subscription.Request with { ResourceId = seeded.Context.DeploymentTargetId.ToUpperInvariant() } }, admin, default));
        Assert.Equal(0, await Demands.CountDocumentsAsync(FilterDefinition<StorageObservationDemandDocument>.Empty));
    }

    [Fact]
    public async Task ConsumerPublishesOnlyPlatformTargetAndVersionHint()
    {
        var changes = new List<RealtimeChange>(); var consumer = new NotifyStorageObservationChangedConsumer((message, _) => { changes.Add(message); return Task.CompletedTask; });
        await consumer.HandleAsync(new(TestData.DefaultDeploymentTargetId, 4, 9));
        var change = Assert.Single(changes); Assert.Equal("storage-observations", change.Topic); Assert.Null(change.TenantId); Assert.Null(change.ResourceGroupId); Assert.Equal(TestData.DefaultDeploymentTargetId, change.ResourceId); Assert.Equal(4, change.Revision); Assert.Equal(9, change.ObservationVersion);
    }

    [Fact]
    public async Task MaximumQualifiedProvisionerPersistsWithoutLeakingIntoIdentityOnlyOutbox()
    {
        var seeded = await SeedAsync(); var owner = await StartAsync(seeded); var inventory = Inventory();
        inventory = inventory with { StorageClasses = [inventory.StorageClasses[0] with { Provisioner = StorageNativeApi.MaximumQualifiedProvisioner }] };
        var committed = Assert.IsType<StorageObservationSnapshot>((await Store().TryCommitSnapshotAsync(owner.Lease, seeded.Context, owner.SourceControlVersion, "source-a", inventory)).Value);
        Assert.True(committed.Complete); Assert.Equal(StorageNativeApi.MaximumQualifiedProvisioner, Assert.Single(committed.StorageClasses).Provisioner);
        var roundTrip = (await Store().LoadFreshSnapshotAsync(seeded.Context))!;
        Assert.Equal(StorageNativeApi.MaximumQualifiedProvisioner, Assert.Single(roundTrip.StorageClasses).Provisioner);
        Assert.Equal(committed.ObservationVersion, roundTrip.ObservationVersion); Assert.Equal(2, await EventCountAsync());
        foreach (var message in await fixture.DbContext.OutboxMessages.Find(item => item.EventType == EventName).ToListAsync())
            Assert.DoesNotContain(StorageNativeApi.MaximumQualifiedProvisioner, message.PayloadJson);
    }

    [Fact]
    public void ProvisionerValidationRetainsQualifiedBoundsAndRejectsArbitraryText()
    {
        var inventory = Inventory();
        var uppercase = StorageNativeApi.MaximumQualifiedProvisioner.ToUpperInvariant();
        Assert.Equal(uppercase, Assert.Single(StorageObservationValueMappings.Sanitize(inventory with { StorageClasses = [inventory.StorageClasses[0] with { Provisioner = uppercase }] }, new())!.StorageClasses).Provisioner);
        var invalid = new[] { new string('a', 64), new string('a', 254) + "/b", "example.com/" + new string('a', 64), "example.com/a/b", "example.com/driver name", "example.com/Мій", "example.com/_driver", "example..com/driver" };
        foreach (var provisioner in invalid)
            Assert.Throws<ArgumentException>(() => StorageObservationValueMappings.Sanitize(inventory with { StorageClasses = [inventory.StorageClasses[0] with { Provisioner = provisioner }] }, new()));
    }

    [Fact]
    public async Task Representative_SanitizedSnapshotAndMinimalPlatformHintCommitTogether()
    {
        var seeded = await SeedAsync(); var owner = await StartAsync(seeded);
        using var trace = TraceContext.Push(TraceContext.Create("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "storage-observation"));
        var snapshot = Assert.IsType<StorageObservationSnapshot>((await Store().TryCommitSnapshotAsync(owner.Lease, seeded.Context, owner.SourceControlVersion, "source-a", Inventory())).Value);
        Assert.Equal(2, snapshot.ObservationVersion); Assert.True(snapshot.Complete); Assert.Single(snapshot.StorageClasses);
        var roundTrip = await Store().LoadFreshSnapshotAsync(seeded.Context);
        Assert.Equal(snapshot.ObservationVersion, roundTrip!.ObservationVersion); Assert.Equal(snapshot.CheckedAtUtc, roundTrip.CheckedAtUtc);
        var envelope = (await fixture.DbContext.OutboxMessages.Find(item => item.EventType == EventName).ToListAsync()).Single(item => item.CorrelationId == "storage-observation");
        var message = JsonSerializer.Deserialize<StorageObservationChangedEvent>(envelope.PayloadJson, EventExtensions.SerializerOptions)!;
        Assert.Equal(seeded.Context.DeploymentTargetId, message.DeploymentTargetId); Assert.Equal(1, message.ConnectionVersion); Assert.Equal(2, message.ObservationVersion);
        Assert.Equal(string.Empty, message.EventCore.TenantId); Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", envelope.TraceId);
        Assert.DoesNotContain("source-a", envelope.PayloadJson); Assert.DoesNotContain("class-a", envelope.PayloadJson);
        var stored = (await Snapshots.Find(FilterDefinition<StorageObservationSnapshotDocument>.Empty).SingleAsync()).ToBsonDocument().ToJson();
        Assert.DoesNotContain("Parameters", stored); Assert.DoesNotContain("Metadata", stored); Assert.DoesNotContain("native-canary", stored);
        var plain = fixture.Database.GetCollection<BsonDocument>(MongoStorageObservationStore.SnapshotsCollection);
        Assert.True((await plain.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync())["Inventory"]["Complete"].AsBoolean);
    }

    [Fact]
    public async Task Representative_OutboxFailureRollsBackSnapshotControlAndTargetFence()
    {
        var seeded = await SeedAsync(); var owner = await StartAsync(seeded);
        var before = await Controls.Find(FilterDefinition<StorageObservationControlDocument>.Empty).SingleAsync();
        var target = await Targets.Find(Target(seeded.Context)).SingleAsync();
        var failing = Store(async (session, writes, ct) => { await fixture.StoreOutboxWrites(session, writes, ct); throw new InvalidOperationException("Injected outbox failure."); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.TryCommitSnapshotAsync(owner.Lease, seeded.Context, owner.SourceControlVersion, "source-a", Inventory()));
        Assert.Equal(before, await Controls.Find(FilterDefinition<StorageObservationControlDocument>.Empty).SingleAsync());
        Assert.Equal(target["ObservationFence"], (await Targets.Find(Target(seeded.Context)).SingleAsync())["ObservationFence"]);
        Assert.Equal(0, await Snapshots.CountDocumentsAsync(FilterDefinition<StorageObservationSnapshotDocument>.Empty)); Assert.Equal(1, await EventCountAsync());
    }

    [Theory]
    [InlineData("rotation")]
    [InlineData("disabled")]
    public async Task Representative_CurrentTargetFenceRejectsOldSnapshotButNegativeCoverageStillCommits(string change)
    {
        var seeded = await SeedAsync(); var owner = await StartAsync(seeded);
        await Store().TryCommitSnapshotAsync(owner.Lease, seeded.Context, owner.SourceControlVersion, "source-a", Inventory());
        await Targets.UpdateOneAsync(Target(seeded.Context), change == "rotation" ? Builders<BsonDocument>.Update.Set("Connection.Version", 2L) : Builders<BsonDocument>.Update.Set("Enabled", false));
        Rejected(StorageObservationRejection.TargetUnavailable, (await Store().TryCommitSnapshotAsync(owner.Lease, seeded.Context, owner.SourceControlVersion, "source-a", Inventory())).Value);
        Assert.Null(await Store().LoadFreshSnapshotAsync(seeded.Context));
        var negative = Assert.IsType<StorageObservationSourceState>((await Store().SetSourceStateAsync(owner.Lease, seeded.Context, owner.SourceControlVersion, false, null)).Value);
        Assert.Equal(owner.SourceControlVersion + 1, negative.SourceControlVersion); Assert.Equal(3, await EventCountAsync());
        await Store().SetSourceStateAsync(owner.Lease, seeded.Context, negative.SourceControlVersion, false, null);
        Assert.Equal(3, await EventCountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Representative_FinalMongoDeadlineRejectsExpiryDuringAwaitedOutbox(bool demandExpires)
    {
        var seeded = await SeedAsync(); var owner = await StartAsync(seeded); var now = await ServerNowAsync();
        if (demandExpires) await Demands.UpdateManyAsync(FilterDefinition<StorageObservationDemandDocument>.Empty, Builders<StorageObservationDemandDocument>.Update.Set(item => item.ExpiresAtUtc, now.AddSeconds(1)));
        else await Controls.UpdateOneAsync(item => item.Id == seeded.Context.DeploymentTargetId, Builders<StorageObservationControlDocument>.Update.Set(item => item.LeaseExpiresAtUtc, now.AddSeconds(1)));
        var before = await Controls.Find(FilterDefinition<StorageObservationControlDocument>.Empty).SingleAsync();
        var delayed = Store(async (session, writes, ct) => { await fixture.StoreOutboxWrites(session, writes, ct); await Task.Delay(1400, ct); });
        Rejected(demandExpires ? StorageObservationRejection.NoDemand : StorageObservationRejection.LeaseLost,
            (await delayed.TryCommitSnapshotAsync(owner.Lease, seeded.Context, owner.SourceControlVersion, "source-a", Inventory())).Value);
        Assert.Equal(before, await Controls.Find(FilterDefinition<StorageObservationControlDocument>.Empty).SingleAsync());
        Assert.Equal(0, await Snapshots.CountDocumentsAsync(FilterDefinition<StorageObservationSnapshotDocument>.Empty)); Assert.Equal(1, await EventCountAsync());
    }

    [Fact]
    public async Task UnchangedHealthyStateCannotAcknowledgeAfterItsFinalStorageAwaitCrossesLeaseExpiry()
    {
        var seeded = await SeedAsync(); var owner = await StartAsync(seeded);
        await Controls.UpdateOneAsync(item => item.Id == seeded.Context.DeploymentTargetId, Builders<StorageObservationControlDocument>.Update.Set(item => item.LeaseExpiresAtUtc, (await ServerNowAsync()).AddSeconds(1)));
        var settings = fixture.Client.Settings.Clone(); var previous = settings.ClusterConfigurator; var updates = 0;
        settings.ClusterConfigurator = builder =>
        {
            previous?.Invoke(builder);
            builder.Subscribe<CommandSucceededEvent>(command => { if (command.CommandName == "update" && Interlocked.Increment(ref updates) == 2) Thread.Sleep(1400); });
        };
        using var client = new MongoClient(settings);
        var delayed = new MongoStorageObservationStore(client.GetDatabase(fixture.Database.DatabaseNamespace.DatabaseName), new MongoRunTransaction(client),
            (request, session, ct) => new MongoFenceDeploymentTargetObservation(client.GetDatabase(fixture.Database.DatabaseNamespace.DatabaseName).GetCollection<Pole.Features.DeploymentTargets.Domain.DeploymentTarget>("DeploymentTargets")).InvokeAsync(request, session, ct), fixture.StoreOutboxWrites, new());
        Rejected(StorageObservationRejection.LeaseLost, (await delayed.SetSourceStateAsync(owner.Lease, seeded.Context, owner.SourceControlVersion, true, "source-a")).Value);
        Assert.Equal(1, await EventCountAsync()); Assert.True(updates >= 2);
    }

    [Fact]
    public async Task Representative_DemandAndLeaseUseServerClockWithoutExtendingOriginalBound()
    {
        var seeded = await SeedAsync(); var now = await ServerNowAsync();
        await Store().PutDemandAsync(seeded.Demand with { ExpiresAtUtc = DateTimeOffset.MaxValue }, seeded.Context);
        var demand = await Demands.Find(FilterDefinition<StorageObservationDemandDocument>.Empty).SingleAsync();
        Assert.InRange(demand.ExpiresAtUtc, now.AddSeconds(60), (await ServerNowAsync()).AddSeconds(60));
        var bound = (await ServerNowAsync()).AddSeconds(10);
        await Store().RenewDemandAsync(seeded.Demand with { ExpiresAtUtc = bound }, seeded.Context);
        await Store().RenewDemandAsync(seeded.Demand with { ExpiresAtUtc = DateTimeOffset.MaxValue }, seeded.Context);
        demand = await Demands.Find(FilterDefinition<StorageObservationDemandDocument>.Empty).SingleAsync();
        Assert.InRange(Math.Abs((demand.OriginalExpiresAtUtc - bound).TotalMilliseconds), 0, 1); Assert.True(demand.ExpiresAtUtc <= demand.OriginalExpiresAtUtc);
        Rejected(StorageObservationRejection.NoDemand, (await Store().PutDemandAsync(seeded.Demand with { Identity = seeded.Demand.Identity with { SubscriptionId = "past" }, ExpiresAtUtc = DateTimeOffset.UnixEpoch }, seeded.Context)).Value);
        Assert.Equal(1, await Demands.CountDocumentsAsync(FilterDefinition<StorageObservationDemandDocument>.Empty));
        now = await ServerNowAsync(); var owner = Assert.IsType<StorageObservationOwnership>((await Store().ClaimLeaseAsync(seeded.Context, "replica-a")).Value);
        Assert.InRange((await Store().LoadControlAsync(owner.Lease.DeploymentTargetId))!.LeaseExpiresAtUtc!.Value, now.AddSeconds(30), (await ServerNowAsync()).AddSeconds(30));
    }

    [Fact]
    public async Task Representative_TwoStoresHaveOneOwnerAndRenewalIsQuiet()
    {
        var seeded = await SeedAsync(); await Store().PutDemandAsync(seeded.Demand, seeded.Context);
        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(index => Store().ClaimLeaseAsync(seeded.Context, $"replica-{index}")));
        var owner = Assert.Single(claims.Select(item => item.Value).OfType<StorageObservationOwnership>());
        Assert.Equal(3, claims.Count(item => item.Value is StorageObservationRejected { Reason: StorageObservationRejection.Busy }));
        var before = await Store().LoadControlAsync(seeded.Context.DeploymentTargetId);
        await Store().RenewLeaseAsync(owner.Lease, seeded.Context); await Store().RenewDemandAsync(seeded.Demand, seeded.Context);
        var after = await Store().LoadControlAsync(seeded.Context.DeploymentTargetId);
        Assert.Equal(before!.Epoch, after!.Epoch); Assert.Equal(before.SourceControlVersion, after.SourceControlVersion); Assert.Equal(before.ObservationVersion, after.ObservationVersion); Assert.Equal(0, await EventCountAsync());
    }

    [Fact]
    public void Representative_LegacyAndSnapshotMappingsDropParametersAndArbitraryMetadataCanaries()
    {
        var mapped = KubernetesStorageDiscovery.MapStorageClass(new V1StorageClass
        {
            Metadata = new V1ObjectMeta { Name = "class-a", Labels = new Dictionary<string, string> { ["app.kubernetes.io/raw"] = "native-canary" }, Annotations = new Dictionary<string, string> { ["pole.io/raw"] = "native-canary", ["storageclass.kubernetes.io/is-default-class"] = "true" } },
            Provisioner = "driver.example",
            Parameters = new Dictionary<string, string> { ["raw"] = "native-canary" }
        });
        Assert.Empty(mapped.Parameters); Assert.Empty(mapped.Metadata); Assert.True(mapped.IsDefault); Assert.DoesNotContain("native-canary", JsonSerializer.Serialize(mapped));
        var safe = StorageObservationValueMappings.Sanitize(new([StorageObservationValueMappings.From(mapped)], [], [], true), new());
        Assert.NotNull(safe); Assert.DoesNotContain("native-canary", safe.ToBsonDocument().ToJson());
        Assert.Null(StorageObservationValueMappings.Sanitize(new([], [], [], false), new()));
        Assert.Null(StorageObservationValueMappings.Sanitize(new([StorageObservationValueMappings.From(mapped), StorageObservationValueMappings.From(mapped)], [], [], true), new() { MaximumItemsPerKind = 1 }));
    }

    private MongoStorageObservationStore Store(StoreOutboxWrites? writes = null) => new(fixture.Database, new MongoRunTransaction(fixture.Client),
        (request, session, ct) => new MongoFenceDeploymentTargetObservation(fixture.DbContext.DeploymentTargets).InvokeAsync(request, session, ct), writes ?? fixture.StoreOutboxWrites, new());
    private async Task<Seeded> SeedAsync()
    {
        await StorageObservationIndexes.CreateAsync(fixture.Database); var id = ObjectId.GenerateNewId().ToString();
        await Targets.InsertOneAsync(new BsonDocument { { "_id", ObjectId.Parse(id) }, { "Enabled", true }, { "DeletionRequestedAtUtc", BsonNull.Value }, { "Connection", new BsonDocument("Version", 1L) }, { "ObservationFence", 0L }, { "ReferenceFence", 0L } });
        return new(new(id, 1), new(new(id, Guid.NewGuid().ToString("N"), "connection-a", "session-a"), DateTimeOffset.UtcNow.AddMinutes(20)));
    }
    private async Task<StorageObservationOwnership> StartAsync(Seeded seeded)
    {
        await Store().PutDemandAsync(seeded.Demand, seeded.Context); var owner = Assert.IsType<StorageObservationOwnership>((await Store().ClaimLeaseAsync(seeded.Context, "replica-a")).Value);
        var source = Assert.IsType<StorageObservationSourceState>((await Store().SetSourceStateAsync(owner.Lease, seeded.Context, owner.SourceControlVersion, true, "source-a")).Value);
        return owner with { SourceControlVersion = source.SourceControlVersion };
    }
    private Task<long> EventCountAsync() => fixture.DbContext.OutboxMessages.CountDocumentsAsync(item => item.EventType == EventName);
    private async Task<DateTime> ServerNowAsync() => (await fixture.Database.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1)))["localTime"].ToUniversalTime();
    private static FilterDefinition<BsonDocument> Target(StorageObservationContext context) => new BsonDocument("_id", ObjectId.Parse(context.DeploymentTargetId));
    private static void Rejected(StorageObservationRejection reason, object? outcome) => Assert.Equal(reason, Assert.IsType<StorageObservationRejected>(outcome).Reason);
    private static StorageInventoryObservation Inventory() => new([new("class-a", "driver.example", true, "Delete", "WaitForFirstConsumer", true)], [], [], true);
    private sealed record Seeded(
        StorageObservationContext Context,
        StorageObservationDemand Demand);
}
