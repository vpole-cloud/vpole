using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Application;
using Pole.Features.DeploymentTargets.Application.Admission;
using Pole.Features.DeploymentTargets.Application.Bootstrap;
using Pole.Features.DeploymentTargets.Application.Catalog;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Credentials;
using Pole.Features.DeploymentTargets.Application.Mongo;
using Pole.Features.DeploymentTargets.Application.Realtime;
using Pole.Features.DeploymentTargets.Application.References;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.DeploymentTargets.Events;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Application.Writes;
using Pole.Features.Outbox.Contracts;
using Pole.Features.Outbox.Domain;
using Pole.Features.Realtime.Contracts;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.DeploymentTargets;

[Collection(IntegrationTestCollection.Global3)]
public sealed class DeploymentTargetRealtimeOutboxTests(MongoReplicaSetFixture fixture)
    : MongoIntegrationTestBase(fixture)
{
    private const string EventType = "pole.deployment-targets/v1/deployment-target-catalog-changed";
    private static readonly DateTime Now = new(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
    public override Task InitializeAsync() => base.InitializeAsync();
    public override Task DisposeAsync() => base.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateAndBothMetadataUpdateBranchesPersistOnlyIdentityAndRevision(bool changingPublicDomain)
    {
        var target = CreateTarget();
        Assert.IsType<DeploymentTarget>((await CreatePersistence().InvokeAsync(target)).Value);
        var replacement = MetadataReplacement(target, changingPublicDomain);
        Assert.IsType<DeploymentTarget>((await UpdatePersistence().InvokeAsync(target, replacement)).Value);
        var messages = await Fixture.DbContext.OutboxMessages.Find(message => message.EventType == EventType)
            .ToListAsync();
        Assert.Equal(2, messages.Count);
        foreach (var message in messages)
        {
            Assert.Null(message.TenantId);
            var payload =
                JsonSerializer.Deserialize<DeploymentTargetCatalogChangedEvent>(message.PayloadJson,
                    EventExtensions.SerializerOptions)!;
            Assert.Equal(target.Id, payload.DeploymentTargetId);
            Assert.Equal("", payload.EventCore.TenantId);
            using var json = JsonDocument.Parse(message.PayloadJson);
            Assert.Equal(new[] { "deploymentTargetId", "eventCore", "revision" },
                json.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
            Assert.DoesNotContain(target.DisplayName, message.PayloadJson);
            Assert.DoesNotContain(target.ClusterIdentity, message.PayloadJson);
            Assert.DoesNotContain("private.example.test", message.PayloadJson);
        }
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("public-update")]
    [InlineData("rotate")]
    [InlineData("validate")]
    [InlineData("fence")]
    [InlineData("delete")]
    [InlineData("bootstrap-create")]
    [InlineData("bootstrap-bind")]
    public async Task OutboxFailureRollsBackEachAcceptedTargetWrite(string operation)
    {
        var target = CreateTarget();
        var creating = operation is "create" or "bootstrap-create";
        if (operation == "delete")
            target = target.RequestDeletion(Now.AddSeconds(1));
        if (!creating)
            await Fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        var before = creating ? null : await ReadAsync(target.Id);
        var realStore = new MongoStoreOutboxWrites(Fixture.Database.GetCollection<OutboxMessage>("OutboxMessages"));
        StoreOutboxWrites failingStore = async (session, writes, ct) =>
        {
            await realStore.InvokeAsync(session, writes, ct);
            throw new InvalidOperationException("controlled target outbox failure");
        };
        switch (operation)
        {
            case "create":
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    CreatePersistence(failingStore).InvokeAsync(target));
                break;
            case "update":
            case "public-update":
                await Assert.ThrowsAsync<InvalidOperationException>(() => UpdatePersistence(failingStore).InvokeAsync(
                    target,
                    MetadataReplacement(target, operation == "public-update")));
                break;
            case "rotate":
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    RotatePersistence(failingStore).InvokeAsync(target, Rotated(target)));
                break;
            case "validate":
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    ValidationPersistence(failingStore).InvokeAsync(target, Observed(target, 1)));
                break;
            case "fence":
            case "delete":
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    DeletePersistence(failingStore).InvokeAsync(target.Id));
                break;
            case "bootstrap-create":
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    BootstrapPersistence(failingStore).InvokeAsync(new CreateFirstDeploymentTargetAsDefault(target)));
                break;
            case "bootstrap-bind":
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    BootstrapPersistence(failingStore)
                        .InvokeAsync(new BindExistingDeploymentTargetAsDefault(target.Id)));
                break;
        }

        var after = await Fixture.DbContext.DeploymentTargets.Find(candidate => candidate.Id == target.Id)
            .SingleOrDefaultAsync();
        if (before is null)
            Assert.Null(after);
        else
        {
            Assert.NotNull(after);
            Assert.Equal(before.Revision, after.Revision);
            Assert.Equal(before.DisplayName, after.DisplayName);
            Assert.Equal(before.AcceptsWorkloads, after.AcceptsWorkloads);
            Assert.Equal(before.DeletionRequestedAtUtc, after.DeletionRequestedAtUtc);
            Assert.Equal(before.ReferenceFence, after.ReferenceFence);
            Assert.Equal(before.Connection.Version, after.Connection.Version);
            Assert.Equal(before.Health, after.Health);
            Assert.Equal(before.Capabilities, after.Capabilities);
            Assert.Equal(before.WorkloadConfiguration.ToBsonDocument(), after.WorkloadConfiguration.ToBsonDocument());
        }

        Assert.Equal(0, await Fixture.DbContext.DeploymentTargetSettings.CountDocumentsAsync(_ => true));
        Assert.Equal(0, await CountEventsAsync());
        Assert.Equal(0,
            await Fixture.Database.GetCollection<DeploymentTargetPublicBaseDomainAdmissionFence>(
                DeploymentTargetPublicBaseDomainAdmissionFence.CollectionName).CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task ConcurrentAndRepeatedValidationEmitOneMeaningfulTransitionAndKeepFreshCheckTime()
    {
        var target = CreateTarget();
        await Fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        var validation = ValidationPersistence();
        var results = await Task.WhenAll(validation.InvokeAsync(target, Observed(target, 1)),
            validation.InvokeAsync(target, Observed(target, 2)));
        Assert.All(results, result => Assert.IsType<DeploymentTarget>(result.Value));
        Assert.Equal(1, await CountEventsAsync());
        // Even this caller's original Unknown snapshot must compare against the current
        // transaction observation rather than publish another false Healthy transition.
        Assert.IsType<DeploymentTarget>((await validation.InvokeAsync(target, Observed(target, 3))).Value);
        var persisted = await ReadAsync(target.Id);
        Assert.Equal(Now.AddSeconds(3), persisted.Health.LastCheckedAtUtc);
        Assert.Equal(Now.AddSeconds(3), persisted.Capabilities.ObservedAtUtc);
        Assert.Equal(target.Revision, persisted.Revision);
        Assert.Equal(1, await CountEventsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidationAndRotationRejectStaleConfigurationWithoutEvents(bool rotating)
    {
        var target = CreateTarget();
        await Fixture.DbContext.DeploymentTargets.InsertOneAsync(target.UpdateMetadata("Changed before result",
            Now.AddSeconds(1)));
        if (rotating)
            Assert.IsType<DeploymentTargetRevisionConflict>(
                (await RotatePersistence().InvokeAsync(target, Rotated(target))).Value);
        else
            Assert.IsType<DeploymentTargetRevisionConflict>(
                (await ValidationPersistence().InvokeAsync(target, Observed(target, 2))).Value);
        Assert.Equal("Changed before result", (await ReadAsync(target.Id)).DisplayName);
        Assert.Equal(0, await CountEventsAsync());
    }

    [Fact]
    public async Task RotationPreservesConcurrentReferenceFenceAndAppendsItsCommittedHint()
    {
        var target = CreateTarget();
        await Fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        await Fixture.DbContext.DeploymentTargets.UpdateOneAsync(candidate => candidate.Id == target.Id,
            Builders<DeploymentTarget>.Update.Set(candidate => candidate.ReferenceFence, 99));
        var persisted =
            Assert.IsType<DeploymentTarget>((await RotatePersistence().InvokeAsync(target, Rotated(target))).Value);
        Assert.Equal(99, persisted.ReferenceFence);
        Assert.Equal(2, persisted.Connection.Version);
        Assert.Equal(2, persisted.Revision);
        Assert.Equal(1, await CountEventsAsync());
    }

    [Fact]
    public async Task DeleteCommitsAdmissionFenceOnceAndThenFinalDeletionAfterReferencesClear()
    {
        var target = CreateTarget();
        await Fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        EnsureNoDeploymentTargetReferences blocked = (id, _) =>
            Task.FromResult<EnsureDeploymentTargetUnreferencedOutcome>(
                new DeploymentTargetHasDurableReferences(id));
        var deleting = DeletePersistence(references: blocked);
        Assert.IsType<DeploymentTargetHasDurableReferences>(Assert
            .IsType<DeleteDeploymentTargetError>((await deleting.InvokeAsync(target.Id)).Value).Value);
        var fenced = await ReadAsync(target.Id);
        Assert.False(fenced.AcceptsWorkloads);
        Assert.NotNull(fenced.DeletionRequestedAtUtc);
        Assert.Equal(1, await CountEventsAsync());
        Assert.IsType<DeleteDeploymentTargetError>((await deleting.InvokeAsync(target.Id)).Value);
        Assert.Equal(1, await CountEventsAsync());
        Assert.IsType<DeploymentTargetDeleted>((await DeletePersistence().InvokeAsync(target.Id)).Value);
        Assert.False(await Fixture.DbContext.DeploymentTargets.Find(candidate => candidate.Id == target.Id).AnyAsync());
        Assert.Equal(2, await CountEventsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BootstrapCreationOrExistingBindingPublishesOnceAndRepeatedBindingIsQuiet(bool bindingExisting)
    {
        var target = CreateTarget();
        if (bindingExisting)
            await Fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        EnsureBootstrapDefaultDeploymentTargetRequest request = bindingExisting
            ? new BindExistingDeploymentTargetAsDefault(target.Id)
            : new CreateFirstDeploymentTargetAsDefault(target);
        var bootstrap = BootstrapPersistence();
        Assert.IsType<BootstrapDefaultDeploymentTargetEnsured>((await bootstrap.InvokeAsync(request)).Value);
        Assert.IsType<BootstrapDefaultDeploymentTargetEnsured>((await bootstrap.InvokeAsync(request)).Value);
        Assert.Equal(1, await CountEventsAsync());
        Assert.Equal(target.Id,
            (await Fixture.DbContext.DeploymentTargetSettings.Find(_ => true).SingleAsync()).DefaultDeploymentTargetId);
    }

    [Fact]
    public async Task IdenticalPersistIsQuietButActualMetadataChangeWithUnchangedRevisionStillNotifies()
    {
        var target = CreateTarget();
        await Fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        Assert.IsType<DeploymentTarget>((await UpdatePersistence().InvokeAsync(target, target)).Value);
        Assert.Equal(0, await CountEventsAsync());
        var changed = target with
        {
            DisplayName = "Legacy caller changed metadata"
        };
        Assert.IsType<DeploymentTarget>((await UpdatePersistence().InvokeAsync(target, changed)).Value);
        Assert.Equal(1, await CountEventsAsync());
        Assert.IsType<DeploymentTarget>((await RotatePersistence().InvokeAsync(changed, changed)).Value);
        Assert.Equal(1, await CountEventsAsync());
    }

    [Fact]
    public async Task ConsumerSeparatesPrivateTargetIdentityAndAllDependentPublicChoiceLists()
    {
        var hints = new List<RealtimeChange>();
        var topics = new List<string>();
        var consumer = new NotifyDeploymentTargetCatalogChangedConsumer((hint, _) =>
            {
                hints.Add(hint);
                return Task.CompletedTask;
            },
            (topic, _) =>
            {
                topics.Add(topic);
                return Task.CompletedTask;
            });
        await consumer.HandleAsync(new DeploymentTargetCatalogChangedEvent("private-target", 8));
        var hint = Assert.Single(hints);
        Assert.Equal("deployment-targets", hint.Topic);
        Assert.Null(hint.TenantId);
        Assert.Equal("private-target", hint.ResourceId);
        Assert.Equal(8, hint.Revision);
        Assert.Equal(new[] { "deployment-target-eligibility", "compute-eligibility", "storage-eligibility" }, topics);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("technical-name")]
    [InlineData("cluster-identity")]
    [InlineData("public-domain")]
    public async Task Create_classifies_its_indexes_using_the_injected_collection_name(string conflict)
    {
        var targets = Fixture.Database.GetCollection<DeploymentTarget>("RenamedDeploymentTargets");
        var original = CreateTarget();
        var candidate = CreateTarget();
        var index = conflict switch
        {
            "technical-name" => DeploymentTargetIndexes.TechnicalName,
            "cluster-identity" => DeploymentTargetIndexes.ClusterIdentity,
            "public-domain" => DeploymentTargetIndexes.ActivePublicBaseDomain,
            _ => null
        };
        if (index is not null)
        {
            var keys = conflict switch
            {
                "technical-name" => Builders<DeploymentTarget>.IndexKeys.Ascending(target => target.TechnicalName),
                "cluster-identity" => Builders<DeploymentTarget>.IndexKeys.Ascending(target => target.ClusterIdentity),
                _ => Builders<DeploymentTarget>.IndexKeys.Ascending(
                    target => target.WorkloadConfiguration.Ingress!.PublicBaseDomain)
            };
            await targets.Indexes.CreateOneAsync(
                new CreateIndexModel<DeploymentTarget>(keys,
                    new CreateIndexOptions { Unique = true, Name = index }));
        }
        if (conflict == "public-domain")
        {
            original = original.Reconfigure(original.DisplayName, true, true,
                DeploymentTargetIngressConfiguration.Create("traefik", "letsencrypt", "example.test"),
                original.WorkloadConfiguration.NetworkSources, Now.AddSeconds(1));
            candidate = candidate.Reconfigure(candidate.DisplayName, true, true,
                DeploymentTargetIngressConfiguration.Create("traefik", "letsencrypt", "example.test"),
                candidate.WorkloadConfiguration.NetworkSources, Now.AddSeconds(1));
        }
        candidate = conflict switch
        {
            "identity" => candidate with { Id = original.Id },
            "technical-name" => candidate with { TechnicalName = original.TechnicalName },
            "cluster-identity" => candidate with { ClusterIdentity = original.ClusterIdentity },
            _ => candidate
        };
        await targets.InsertOneAsync(original);
        var persistence = new MongoPersistCreatedDeploymentTarget(targets,
            NoOverlap(), Run().InvokeOutcomeAsync<PersistCreatedDeploymentTargetOutcome>, Store());

        var outcome = await persistence.InvokeAsync(candidate);
        var error = Assert.IsType<PersistCreatedDeploymentTargetError>(outcome.Value);
        if (conflict == "technical-name")
            Assert.IsType<DeploymentTargetTechnicalNameConflict>(error.Value);
        else if (conflict == "public-domain")
            Assert.IsType<DeploymentTargetPublicBaseDomainConflict>(error.Value);
        else
            Assert.IsType<DeploymentTargetIdentityConflict>(error.Value);
        Assert.Equal(1, await targets.CountDocumentsAsync(FilterDefinition<DeploymentTarget>.Empty));
        Assert.Equal(0, await CountEventsAsync());
    }

    [Theory]
    [InlineData("_id_")]
    [InlineData(DeploymentTargetIndexes.TechnicalName)]
    [InlineData(DeploymentTargetIndexes.ClusterIdentity)]
    [InlineData(DeploymentTargetIndexes.ActivePublicBaseDomain)]
    public async Task Duplicate_indexes_in_another_transaction_collection_are_not_target_conflicts(string index)
    {
        var target = CreateTarget();
        var other = Fixture.Database.GetCollection<BsonDocument>("OtherTransactionWrites");
        if (index != "_id_")
            await other.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("Value"),
                    new CreateIndexOptions { Unique = true, Name = index }));
        await other.InsertOneAsync(new BsonDocument { ["_id"] = "occupied", ["Value"] = "duplicate" });
        StoreOutboxWrites failInOtherCollection = async (session, _, ct) =>
            await other.InsertOneAsync(session,
                new BsonDocument { ["_id"] = index == "_id_" ? "occupied" : "other", ["Value"] = "duplicate" },
                cancellationToken: ct);

        await Assert.ThrowsAsync<MongoWriteException>(() =>
            CreatePersistence(failInOtherCollection).InvokeAsync(target));

        Assert.Null(await Fixture.DbContext.DeploymentTargets
            .Find(item => item.Id == target.Id).SingleOrDefaultAsync());
        Assert.Equal(0, await CountEventsAsync());
    }

    private MongoPersistCreatedDeploymentTarget CreatePersistence(StoreOutboxWrites? store = null) => new(
        Fixture.DbContext.DeploymentTargets,
        NoOverlap(),
        Run().InvokeOutcomeAsync<PersistCreatedDeploymentTargetOutcome>,
        store ?? Store());

    private MongoPersistUpdatedDeploymentTarget UpdatePersistence(StoreOutboxWrites? store = null) => new(
        Fixture.DbContext.DeploymentTargets,
        new MongoEnsureNoDeploymentTargetPublicBaseDomainReferences(Fixture.DbContext.DeploymentTargetReferences)
            .InvokeAsync,
        NoOverlap(),
        Run().InvokeOutcomeAsync<PersistUpdatedDeploymentTargetOutcome>,
        store ?? Store());

    private MongoPersistRotatedDeploymentTargetConnection RotatePersistence(StoreOutboxWrites? store = null) => new(
        Fixture.DbContext.DeploymentTargets,
        Run().InvokeOutcomeAsync<PersistRotatedDeploymentTargetConnectionOutcome>,
        store ?? Store());

    private MongoPersistDeploymentTargetValidation ValidationPersistence(StoreOutboxWrites? store = null) => new(
        Fixture.DbContext.DeploymentTargets,
        Run().InvokeOutcomeAsync<PersistDeploymentTargetValidationOutcome>,
        store ?? Store());

    private MongoDeleteDeploymentTarget DeletePersistence(StoreOutboxWrites? store = null,
        EnsureNoDeploymentTargetReferences? references = null) =>
        new(Fixture.DbContext.DeploymentTargets,
            Fixture.DbContext.DeploymentTargetSettings,
            references ?? new MongoEnsureNoDeploymentTargetReferences(Fixture.DbContext.DeploymentTargetReferences)
                .InvokeAsync,
            Run().InvokeOutcomeAsync<DeleteDeploymentTargetPersistenceOutcome>,
            TimeProvider.System,
            Run().InvokeOutcomeAsync<PersistDeploymentTargetDeletionFenceOutcome>,
            store ?? Store());

    private MongoEnsureBootstrapDefaultDeploymentTarget BootstrapPersistence(StoreOutboxWrites? store = null) => new(
        Fixture.DbContext.DeploymentTargets,
        Fixture.DbContext.DeploymentTargetSettings,
        NoOverlap(),
        Run().InvokeOutcomeAsync<EnsureBootstrapDefaultDeploymentTargetOutcome>,
        TimeProvider.System,
        store ?? Store());

    private EnsureNoOverlappingDeploymentTargetPublicBaseDomain NoOverlap() =>
        new MongoEnsureNoOverlappingDeploymentTargetPublicBaseDomain(
            Fixture.DbContext.DeploymentTargets,
            Fixture.Database.GetCollection<DeploymentTargetPublicBaseDomainAdmissionFence>(
                DeploymentTargetPublicBaseDomainAdmissionFence.CollectionName)).InvokeAsync;

    private MongoRunTransaction Run() => new(Fixture.Client);
    private StoreOutboxWrites Store() => new MongoStoreOutboxWrites(Fixture.Database.GetCollection<OutboxMessage>(
        "OutboxMessages")).InvokeAsync;
    private Task<long> CountEventsAsync() => Fixture.DbContext.CountAllOutboxMessagesAsync(EventType);

    private Task<DeploymentTarget> ReadAsync(string id) =>
        Fixture.DbContext.DeploymentTargets.Find(target => target.Id == id).SingleAsync();

    private static DeploymentTarget CreateTarget()
    {
        var id = ObjectId.GenerateNewId().ToString();
        return DeploymentTargetFactory.Create("target-" + id, "Private target name",
            $"private-cluster:{id}",
            DeploymentTargetConnectionFactory.CreateInCluster(1),
            DeploymentTargetHealth.Unknown(1),
            DeploymentTargetCapabilities.Unknown(1),
            DeploymentTargetTestData.CreateWorkloadConfiguration(),
            enabled: true,
            acceptsWorkloads: true,
            new DeploymentTargetFactory.CreateOptions(id, Now));
    }

    private static DeploymentTarget MetadataReplacement(DeploymentTarget target, bool publicDomain) =>
        target.Reconfigure(
            "Updated private target",
            target.Enabled,
            target.AcceptsWorkloads,
            publicDomain
                ? DeploymentTargetIngressConfiguration.Create("traefik", "letsencrypt-prod", "private.example.test")
                : target.WorkloadConfiguration.Ingress,
            target.WorkloadConfiguration.NetworkSources,
            Now.AddSeconds(1));

    private static DeploymentTarget Rotated(DeploymentTarget target) => target.ReplaceConnectionCredential(
        target.ClusterIdentity,
        DeploymentTargetConnectionFactory.CreateInCluster(2),
        Now.AddSeconds(1));

    private static DeploymentTarget Observed(DeploymentTarget target, int seconds) => target.ApplyValidationObservation(
        target.Revision,
        target.Connection.Version,
        DeploymentTargetHealth.Healthy(target.Connection.Version, Now.AddSeconds(seconds)),
        DeploymentTargetCapabilities.Observed(target.Connection.Version,
            Now.AddSeconds(seconds),
            "v1.35.0",
            new DeploymentTargetNamespaceCapabilities(true, true, true, true, true),
            new DeploymentTargetNetworkPolicyCapabilities(true, true, true, true, true)));
}
