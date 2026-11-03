using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Migrations;
using Pole.Features.Domains.Application.Persistence;
using Pole.Features.Domains.Application.Persistence.DomainZones;
using Pole.Features.Domains.Application.Persistence.HostnameBindings;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Features.Tenants.Contracts;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.Domains;

[Collection(IntegrationTestCollection.Name)]
public sealed class DomainBatchCapabilityTests(MongoReplicaSetFixture fixture) : IAsyncLifetime
{
    private static readonly ResourceTypeId WebServiceType =
        ResourceTypeId.Create("pole/web-service");

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Generated_sets_preserve_order_batch_reads_and_reuse_existing_zones()
    {
        await PrepareDatabaseAsync();
        var tenantId = ObjectId.GenerateNewId().ToString();
        var firstTargetId = ObjectId.GenerateNewId().ToString();
        var secondTargetId = ObjectId.GenerateNewId().ToString();
        var coordinator = new RecordingTargetCoordinator();
        var capabilities = CreateCapabilities(tenantId, coordinator);
        var zoneRequests = new[]
        {
            new GetOrCreateGeneratedZoneRequest(
                tenantId,
                secondTargetId,
                "second.example.com",
                ExpectedWorkloadConfigurationRevision: 7),
            new GetOrCreateGeneratedZoneRequest(
                tenantId,
                firstTargetId,
                "first.example.com",
                ExpectedWorkloadConfigurationRevision: 3)
        };
        var transaction = new MongoRunTransaction(fixture.Client);

        var created = Assert.IsAssignableFrom<IReadOnlyList<TenantDomainZoneSnapshot>>(
            (await transaction.InvokeOutcomeAsync<GetOrCreateGeneratedDomainZonesOutcome>((transactionContext,
                cancellationToken) => CompleteAsync(
                transactionContext,
                capabilities.GetOrCreateGeneratedSetOutcomeAsync(
                    zoneRequests,
                    transactionContext.Session,
                    cancellationToken),
                outcome => outcome is IReadOnlyList<TenantDomainZoneSnapshot>))).Value);

        Assert.Equal([secondTargetId, firstTargetId], created.Select(zone => zone.DeploymentTargetId));
        Assert.Equal(2, await fixture.DbContext.CountOutboxMessagesAsync("pole.domains/v1/domain-metadata-changed"));
        Assert.Equal(1, coordinator.FenceBatchCalls);
        Assert.Equal([secondTargetId, firstTargetId], coordinator.Fences.Select(item => item.DeploymentTargetId));
        Assert.Equal([secondTargetId, firstTargetId], coordinator.Registrations.Select(item => item.TargetId));

        var reused = Assert.IsAssignableFrom<IReadOnlyList<TenantDomainZoneSnapshot>>(
            (await transaction.InvokeOutcomeAsync<GetOrCreateGeneratedDomainZonesOutcome>((transactionContext,
                cancellationToken) => CompleteAsync(
                transactionContext,
                capabilities.GetOrCreateGeneratedSetOutcomeAsync(
                    zoneRequests,
                    transactionContext.Session,
                    cancellationToken),
                outcome => outcome is IReadOnlyList<TenantDomainZoneSnapshot>))).Value);

        Assert.Equal(created.Select(zone => zone.Id), reused.Select(zone => zone.Id));
        Assert.Equal(2, await fixture.DbContext.CountOutboxMessagesAsync("pole.domains/v1/domain-metadata-changed"));
        Assert.Equal(1, coordinator.FenceBatchCalls);
        Assert.Equal(2, coordinator.Registrations.Count);

        var resourceId = ObjectId.GenerateNewId().ToString();
        var deploymentIds = new[]
        {
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString()
        };
        var bindings = Assert.IsAssignableFrom<IReadOnlyList<HostnameBindingSnapshot>>(
            (await transaction.InvokeOutcomeAsync<CreateGeneratedHostnameBindingsOutcome>((transactionContext,
                cancellationToken) => CompleteAsync(
                transactionContext,
                capabilities.CreateGeneratedSetOutcomeAsync(
                    [
                        CreateBindingRequest(created[1], deploymentIds[1], resourceId),
                        CreateBindingRequest(created[0], deploymentIds[0], resourceId)
                    ],
                    transactionContext.Session,
                    cancellationToken),
                outcome => outcome is IReadOnlyList<HostnameBindingSnapshot>))).Value);

        Assert.Equal([deploymentIds[1], deploymentIds[0]], bindings.Select(binding => binding.ResourceDeploymentId));
        Assert.Equal(
            [
                $"webservice-{resourceId}.{created[1].Domain}",
                $"webservice-{resourceId}.{created[0].Domain}"
            ],
            bindings.Select(binding => binding.Hostname));
        Assert.Equal(2,
            await fixture.Database
                .GetCollection<TenantDomainZone>("TenantDomainZones")
                .CountDocumentsAsync(Builders<TenantDomainZone>.Filter.Empty));
        Assert.Equal(2,
            await fixture.Database
                .GetCollection<HostnameBinding>("HostnameBindings")
                .CountDocumentsAsync(Builders<HostnameBinding>.Filter.Empty));
        var persistedZones = await fixture.Database
            .GetCollection<TenantDomainZone>("TenantDomainZones")
            .Find(Builders<TenantDomainZone>.Filter.Empty)
            .ToListAsync();
        Assert.All(persistedZones, persisted => Assert.Equal(2, persisted.Revision));
    }

    [Fact]
    public async Task Duplicate_set_inputs_fail_before_any_persistence()
    {
        await PrepareDatabaseAsync();
        var tenantId = ObjectId.GenerateNewId().ToString();
        var targetId = ObjectId.GenerateNewId().ToString();
        var coordinator = new RecordingTargetCoordinator();
        var capabilities = CreateCapabilities(tenantId, coordinator);
        var request = new GetOrCreateGeneratedZoneRequest(
            tenantId,
            targetId,
            "example.com",
            ExpectedWorkloadConfigurationRevision: 1);
        var transaction = new MongoRunTransaction(fixture.Client);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            transaction.InvokeOutcomeAsync<GetOrCreateGeneratedDomainZonesOutcome>((transactionContext,
                cancellationToken) => CompleteAsync(
                transactionContext,
                capabilities.GetOrCreateGeneratedSetOutcomeAsync(
                    [request, request],
                    transactionContext.Session,
                    cancellationToken),
                outcome => outcome is IReadOnlyList<TenantDomainZoneSnapshot>)));

        Assert.Equal(0, coordinator.FenceBatchCalls);
        Assert.Empty(coordinator.Registrations);
        Assert.Equal(0,
            await fixture.Database
                .GetCollection<TenantDomainZone>("TenantDomainZones")
                .CountDocumentsAsync(Builders<TenantDomainZone>.Filter.Empty));
    }

    [Fact]
    public async Task Generated_binding_set_rejects_cross_target_zone_without_partial_writes()
    {
        await PrepareDatabaseAsync();
        var tenantId = ObjectId.GenerateNewId().ToString();
        var targetId = ObjectId.GenerateNewId().ToString();
        var otherTargetId = ObjectId.GenerateNewId().ToString();
        var capabilities = CreateCapabilities(tenantId, new RecordingTargetCoordinator());
        var transaction = new MongoRunTransaction(fixture.Client);
        var zones = Assert.IsAssignableFrom<IReadOnlyList<TenantDomainZoneSnapshot>>(
            (await transaction.InvokeOutcomeAsync<GetOrCreateGeneratedDomainZonesOutcome>((transactionContext,
                cancellationToken) => CompleteAsync(
                transactionContext,
                capabilities.GetOrCreateGeneratedSetOutcomeAsync(
                    [new GetOrCreateGeneratedZoneRequest(tenantId, targetId, "example.com", 1)],
                    transactionContext.Session,
                    cancellationToken),
                outcome => outcome is IReadOnlyList<TenantDomainZoneSnapshot>))).Value);
        var resourceId = ObjectId.GenerateNewId().ToString();
        var valid = CreateBindingRequest(
            zones[0],
            ObjectId.GenerateNewId().ToString(),
            resourceId);
        var invalid = valid with
        {
            ResourceDeploymentId = ObjectId.GenerateNewId().ToString(),
            DeploymentTargetId = otherTargetId
        };

        var result =
            await transaction.InvokeOutcomeAsync<CreateGeneratedHostnameBindingsOutcome>((transactionContext,
                cancellationToken) => CompleteAsync(
                transactionContext,
                capabilities.CreateGeneratedSetOutcomeAsync(
                    [valid, invalid],
                    transactionContext.Session,
                    cancellationToken),
                outcome => outcome is IReadOnlyList<HostnameBindingSnapshot>));

        var error = Assert.IsType<GeneratedHostnameBindingError>(result.Value);
        Assert.IsType<GeneratedHostnameBindingTargetMismatch>(error.Value);
        Assert.Equal(0,
            await fixture.Database
                .GetCollection<HostnameBinding>("HostnameBindings")
                .CountDocumentsAsync(Builders<HostnameBinding>.Filter.Empty));
    }

    [Fact]
    public async Task Finalizing_last_binding_releases_generated_zone_and_target_reference()
    {
        await PrepareDatabaseAsync();
        var tenantId = ObjectId.GenerateNewId().ToString();
        var targetId = ObjectId.GenerateNewId().ToString();
        var coordinator = new RecordingTargetCoordinator();
        var capabilities = CreateCapabilities(tenantId, coordinator);
        var transaction = new MongoRunTransaction(fixture.Client);
        var zone = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<TenantDomainZoneSnapshot>>(
            (await transaction.InvokeOutcomeAsync<GetOrCreateGeneratedDomainZonesOutcome>((transactionContext,
                cancellationToken) => CompleteAsync(
                transactionContext,
                capabilities.GetOrCreateGeneratedSetOutcomeAsync(
                    [new GetOrCreateGeneratedZoneRequest(tenantId, targetId, "example.com", 1)],
                    transactionContext.Session,
                    cancellationToken),
                outcome => outcome is IReadOnlyList<TenantDomainZoneSnapshot>))).Value));
        var resourceId = ObjectId.GenerateNewId().ToString();
        var deploymentId = ObjectId.GenerateNewId().ToString();
        Assert.IsAssignableFrom<IReadOnlyList<HostnameBindingSnapshot>>(
            (await transaction.InvokeOutcomeAsync<CreateGeneratedHostnameBindingsOutcome>((transactionContext,
                cancellationToken) => CompleteAsync(
                transactionContext,
                capabilities.CreateGeneratedSetOutcomeAsync(
                    [CreateBindingRequest(zone, deploymentId, resourceId)],
                    transactionContext.Session,
                    cancellationToken),
                outcome => outcome is IReadOnlyList<HostnameBindingSnapshot>))).Value);

        Assert.IsType<HostnameBindingsCleanedUp>(
            (await transaction.InvokeOutcomeAsync<CleanupHostnameBindingsOutcome>((transactionContext,
                cancellationToken) => CompleteAsync(
                transactionContext,
                capabilities.DeleteAllForDeploymentAsync(
                    new DeleteHostnameBindingsForDeploymentRequest(
                        TenantId: tenantId,
                        ResourceType: WebServiceType,
                        ResourceId: resourceId,
                        ResourceDeploymentId: deploymentId),
                    session: transactionContext.Session,
                    cancellationToken: cancellationToken),
                outcome => outcome is HostnameBindingsCleanedUp))).Value);

        Assert.Equal(0,
            await fixture.Database
                .GetCollection<HostnameBinding>("HostnameBindings")
                .CountDocumentsAsync(Builders<HostnameBinding>.Filter.Empty));
        Assert.Equal(0,
            await fixture.Database
                .GetCollection<TenantDomainZone>("TenantDomainZones")
                .CountDocumentsAsync(Builders<TenantDomainZone>.Filter.Empty));
        var unregistration = Assert.Single(coordinator.Unregistrations);
        Assert.Equal(targetId, unregistration.TargetId);
        Assert.Equal(zone.Id, unregistration.Owner.Id);
        Assert.Equal(4, await fixture.DbContext.CountOutboxMessagesAsync("pole.domains/v1/domain-metadata-changed"));
    }

    [Fact]
    public async Task Finalizing_last_binding_aborts_when_target_rejects_zone_reference_removal()
    {
        await PrepareDatabaseAsync();
        var tenantId = ObjectId.GenerateNewId().ToString();
        var targetId = ObjectId.GenerateNewId().ToString();
        var coordinator = new RecordingTargetCoordinator();
        var capabilities = CreateCapabilities(tenantId, coordinator);
        var transaction = new MongoRunTransaction(fixture.Client);
        var zone = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<TenantDomainZoneSnapshot>>(
            (await transaction.InvokeOutcomeAsync<GetOrCreateGeneratedDomainZonesOutcome>((transactionContext,
                cancellationToken) => CompleteAsync(
                transactionContext,
                capabilities.GetOrCreateGeneratedSetOutcomeAsync(
                    [
                        new GetOrCreateGeneratedZoneRequest(
                            tenantId,
                            targetId,
                            "example.com",
                            ExpectedWorkloadConfigurationRevision: 1)
                    ],
                    transactionContext.Session,
                    cancellationToken),
                outcome => outcome is IReadOnlyList<TenantDomainZoneSnapshot>))).Value));
        var resourceId = ObjectId.GenerateNewId().ToString();
        var deploymentId = ObjectId.GenerateNewId().ToString();
        Assert.IsAssignableFrom<IReadOnlyList<HostnameBindingSnapshot>>(
            (await transaction.InvokeOutcomeAsync<CreateGeneratedHostnameBindingsOutcome>((transactionContext,
                cancellationToken) => CompleteAsync(
                transactionContext,
                capabilities.CreateGeneratedSetOutcomeAsync(
                    [CreateBindingRequest(zone, deploymentId, resourceId)],
                    transactionContext.Session,
                    cancellationToken),
                outcome => outcome is IReadOnlyList<HostnameBindingSnapshot>))).Value);
        coordinator.RejectReferenceRemoval = true;

        var cleanup =
            await transaction.InvokeOutcomeAsync<CleanupHostnameBindingsOutcome>((transactionContext,
                cancellationToken) => CompleteAsync(
                transactionContext,
                capabilities.DeleteAllForDeploymentAsync(
                    new DeleteHostnameBindingsForDeploymentRequest(
                        TenantId: tenantId,
                        ResourceType: WebServiceType,
                        ResourceId: resourceId,
                        ResourceDeploymentId: deploymentId),
                    session: transactionContext.Session,
                    cancellationToken: cancellationToken),
                outcome => outcome is HostnameBindingsCleanedUp));

        var error = Assert.IsType<HostnameBindingCleanupError>(cleanup.Value).Value;
        Assert.IsType<HostnameBindingCleanupReferenceRemovalRejected>(error);
        Assert.Equal(2, await fixture.DbContext.CountOutboxMessagesAsync("pole.domains/v1/domain-metadata-changed"));
        Assert.Equal(1,
            await fixture.Database
                .GetCollection<HostnameBinding>("HostnameBindings")
                .CountDocumentsAsync(Builders<HostnameBinding>.Filter.Empty));
        Assert.Equal(1,
            await fixture.Database
                .GetCollection<TenantDomainZone>("TenantDomainZones")
                .CountDocumentsAsync(Builders<TenantDomainZone>.Filter.Empty));
    }

    [Fact]
    public async Task Finalizing_one_deployment_keeps_shared_generated_zone_while_another_binding_exists()
    {
        await PrepareDatabaseAsync();
        var tenantId = ObjectId.GenerateNewId().ToString();
        var targetId = ObjectId.GenerateNewId().ToString();
        var coordinator = new RecordingTargetCoordinator();
        var capabilities = CreateCapabilities(tenantId, coordinator);
        var transaction = new MongoRunTransaction(fixture.Client);
        var zone = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<TenantDomainZoneSnapshot>>(
            (await transaction.InvokeOutcomeAsync<GetOrCreateGeneratedDomainZonesOutcome>((transactionContext,
                cancellationToken) => CompleteAsync(
                transactionContext,
                capabilities.GetOrCreateGeneratedSetOutcomeAsync(
                    [new GetOrCreateGeneratedZoneRequest(tenantId, targetId, "example.com", 1)],
                    transactionContext.Session,
                    cancellationToken),
                outcome => outcome is IReadOnlyList<TenantDomainZoneSnapshot>))).Value));
        var firstResourceId = ObjectId.GenerateNewId().ToString();
        var secondResourceId = ObjectId.GenerateNewId().ToString();
        var firstDeploymentId = ObjectId.GenerateNewId().ToString();
        var secondDeploymentId = ObjectId.GenerateNewId().ToString();
        Assert.IsAssignableFrom<IReadOnlyList<HostnameBindingSnapshot>>(
            (await transaction.InvokeOutcomeAsync<CreateGeneratedHostnameBindingsOutcome>((transactionContext,
                cancellationToken) => CompleteAsync(
                transactionContext,
                capabilities.CreateGeneratedSetOutcomeAsync(
                    [CreateBindingRequest(zone, firstDeploymentId, firstResourceId)],
                    transactionContext.Session,
                    cancellationToken),
                outcome => outcome is IReadOnlyList<HostnameBindingSnapshot>))).Value);
        Assert.IsAssignableFrom<IReadOnlyList<HostnameBindingSnapshot>>(
            (await transaction.InvokeOutcomeAsync<CreateGeneratedHostnameBindingsOutcome>((transactionContext,
                cancellationToken) => CompleteAsync(
                transactionContext,
                capabilities.CreateGeneratedSetOutcomeAsync(
                    [CreateBindingRequest(zone, secondDeploymentId, secondResourceId)],
                    transactionContext.Session,
                    cancellationToken),
                outcome => outcome is IReadOnlyList<HostnameBindingSnapshot>))).Value);

        Assert.IsType<HostnameBindingsCleanedUp>(
            (await transaction.InvokeOutcomeAsync<CleanupHostnameBindingsOutcome>((transactionContext,
                cancellationToken) => CompleteAsync(
                transactionContext,
                capabilities.DeleteAllForDeploymentAsync(
                    new DeleteHostnameBindingsForDeploymentRequest(
                        TenantId: tenantId,
                        ResourceType: WebServiceType,
                        ResourceId: firstResourceId,
                        ResourceDeploymentId: firstDeploymentId),
                    session: transactionContext.Session,
                    cancellationToken: cancellationToken),
                outcome => outcome is HostnameBindingsCleanedUp))).Value);

        Assert.Equal(1,
            await fixture.Database
                .GetCollection<HostnameBinding>("HostnameBindings")
                .CountDocumentsAsync(Builders<HostnameBinding>.Filter.Empty));
        Assert.Equal(1,
            await fixture.Database
                .GetCollection<TenantDomainZone>("TenantDomainZones")
                .CountDocumentsAsync(Builders<TenantDomainZone>.Filter.Empty));
        var remainingZone = await fixture.Database
            .GetCollection<TenantDomainZone>("TenantDomainZones")
            .Find(item => item.Id == zone.Id)
            .SingleAsync();
        Assert.Equal(4, remainingZone.Revision);
        Assert.Empty(coordinator.Unregistrations);
    }

    private DomainCapabilities CreateCapabilities(
        string tenantId,
        RecordingTargetCoordinator coordinator)
    {
        var cleanup = new MongoHostnameBindingCleanup(
            fixture.Database.GetCollection<TenantDomainZone>("TenantDomainZones"),
            fixture.Database.GetCollection<HostnameBinding>("HostnameBindings"),
            coordinator.ApplyChangesOutcomeAsync,
            TimeProvider.System,
            fixture.StoreOutboxWrites);
        var zones = new MongoGetOrCreateGeneratedDomainZones(
            fixture.Database.GetCollection<TenantDomainZone>("TenantDomainZones"),
            coordinator.ApplyChangesOutcomeAsync,
            coordinator.FenceManyOutcomeAsync,
            FixedTenantIdentityLookup(tenantId),
            TimeProvider.System,
            fixture.StoreOutboxWrites);
        var bindings = new MongoCreateGeneratedHostnameBindings(
            fixture.Database.GetCollection<TenantDomainZone>("TenantDomainZones"),
            fixture.Database.GetCollection<HostnameBinding>("HostnameBindings"),
            cleanup,
            TimeProvider.System,
            fixture.StoreOutboxWrites);
        var deleteBindings = new MongoDeleteHostnameBindingsForDeployment(cleanup);
        return new DomainCapabilities(
            zones.InvokeAsync,
            bindings.InvokeAsync,
            deleteBindings.InvokeAsync);
    }

    private sealed record DomainCapabilities(
        GetOrCreateGeneratedDomainZones GetOrCreateGeneratedSetOutcomeAsync,
        CreateGeneratedHostnameBindings CreateGeneratedSetOutcomeAsync,
        DeleteHostnameBindingsForDeployment DeleteAllForDeploymentAsync);

    private static async Task<MongoTransactionDecision<TOutcome>> CompleteAsync<TOutcome>(
        MongoTransactionContext<TOutcome> transaction,
        Task<TOutcome> operation,
        Func<TOutcome, bool> succeeded)
        where TOutcome : notnull
    {
        var outcome = await operation;
        return succeeded(outcome) ? transaction.Commit(outcome) : transaction.Abort(outcome);
    }

    private static CreateGeneratedHostnameBindingRequest CreateBindingRequest(
        TenantDomainZoneSnapshot zone,
        string deploymentId,
        string resourceId) =>
        new(
            zone.TenantId,
            zone.Id,
            zone.DeploymentTargetId,
            WebServiceType,
            resourceId,
            deploymentId,
            $"webservice-{resourceId}");

    private async Task PrepareDatabaseAsync()
    {
        await new TenantDomainZoneSchemaValidationMigration().Up(fixture.Database);
        await new HostnameBindingSchemaValidationMigration().Up(fixture.Database);
        await new DomainIndexesMigration().Up(fixture.Database);
    }

    private static LoadTenantIdentity FixedTenantIdentityLookup(string tenantId) =>
        (requestedTenantId, _) => Task.FromResult<LoadTenantIdentityOutcome>(
            string.Equals(requestedTenantId, tenantId, StringComparison.Ordinal)
                ? new TenantIdentitySnapshot(tenantId, "Acme Corporation")
                : new TenantIdentityNotFound(requestedTenantId));

    private sealed class RecordingTargetCoordinator
    {
        public int FenceBatchCalls { get; private set; }

        public bool RejectReferenceRemoval { get; set; }

        public List<DeploymentTargetWorkloadConfigurationFenceRequest> Fences { get; } = [];

        public List<(string TargetId, DeploymentTargetReferenceOwner Owner)> Registrations { get; } = [];

        public List<(string TargetId, DeploymentTargetReferenceOwner Owner)> Unregistrations { get; } = [];

        public Task<FenceDeploymentTargetWorkloadConfigurationsOutcome> FenceManyOutcomeAsync(
            IReadOnlyList<DeploymentTargetWorkloadConfigurationFenceRequest> requests,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
        {
            Assert.True(session.IsInTransaction);
            FenceBatchCalls++;
            Fences.AddRange(requests);
            return Task.FromResult<FenceDeploymentTargetWorkloadConfigurationsOutcome>(
                new DeploymentTargetWorkloadConfigurationsFenced());
        }

        public Task<ApplyDeploymentTargetReferenceChangesOutcome> ApplyChangesOutcomeAsync(
            ApplyDeploymentTargetReferenceChangesRequest request,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
        {
            Assert.True(session.IsInTransaction);
            Registrations.AddRange(request.AddedDeploymentTargetIds.Select(id => (id, request.Owner)));
            Unregistrations.AddRange(request.RemovedDeploymentTargetIds.Select(id => (id, request.Owner)));
            if (RejectReferenceRemoval && request.RemovedDeploymentTargetIds.Count > 0)
            {
                return Task.FromResult<ApplyDeploymentTargetReferenceChangesOutcome>(
                    new ApplyDeploymentTargetReferenceChangesOutcome(
                        new DeploymentTargetReferenceChangeRejected(
                            request.Owner,
                            request.RemovedDeploymentTargetIds.ToArray())));
            }

            return Task.FromResult<ApplyDeploymentTargetReferenceChangesOutcome>(
                new DeploymentTargetReferencesChanged());
        }
    }
}
