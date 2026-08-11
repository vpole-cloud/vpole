using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Application.Migrations;
using Pole.Features.Domains.Application.Persistence;
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

        var created = (await transaction.InvokeAsync<IReadOnlyList<TenantDomainZoneSnapshot>>(
            (session, cancellationToken) => capabilities.GetOrCreateGeneratedSetAsync(
                zoneRequests,
                session,
                cancellationToken))).AssertSuccess();

        Assert.Equal([secondTargetId, firstTargetId], created.Select(zone => zone.DeploymentTargetId));
        Assert.Equal(1, coordinator.FenceBatchCalls);
        Assert.Equal([secondTargetId, firstTargetId], coordinator.Fences.Select(item => item.DeploymentTargetId));
        Assert.Equal([secondTargetId, firstTargetId], coordinator.Registrations.Select(item => item.TargetId));

        var reused = (await transaction.InvokeAsync<IReadOnlyList<TenantDomainZoneSnapshot>>(
            (session, cancellationToken) => capabilities.GetOrCreateGeneratedSetAsync(
                zoneRequests,
                session,
                cancellationToken))).AssertSuccess();

        Assert.Equal(created.Select(zone => zone.Id), reused.Select(zone => zone.Id));
        Assert.Equal(1, coordinator.FenceBatchCalls);
        Assert.Equal(2, coordinator.Registrations.Count);

        var resourceId = ObjectId.GenerateNewId().ToString();
        var deploymentIds = new[]
        {
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString()
        };
        var bindings = (await transaction.InvokeAsync<IReadOnlyList<HostnameBindingSnapshot>>(
            (session, cancellationToken) => capabilities.CreateGeneratedSetAsync(
                [
                    CreateBindingRequest(created[1], deploymentIds[1], resourceId),
                    CreateBindingRequest(created[0], deploymentIds[0], resourceId)
                ],
                session,
                cancellationToken))).AssertSuccess();

        Assert.Equal([deploymentIds[1], deploymentIds[0]], bindings.Select(binding => binding.ResourceDeploymentId));
        Assert.Equal(
            [
                $"webservice-{resourceId}.{created[1].Domain}",
                $"webservice-{resourceId}.{created[0].Domain}"
            ],
            bindings.Select(binding => binding.Hostname));
        Assert.Equal(2, await fixture.Database
            .GetCollection<TenantDomainZone>("TenantDomainZones")
            .CountDocumentsAsync(Builders<TenantDomainZone>.Filter.Empty));
        Assert.Equal(2, await fixture.Database
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

        var result = await transaction.InvokeAsync<IReadOnlyList<TenantDomainZoneSnapshot>>(
            (session, cancellationToken) => capabilities.GetOrCreateGeneratedSetAsync(
                [request, request],
                session,
                cancellationToken));

        Assert.IsType<ValidationFailure>(result.AssertFailure());
        Assert.Equal(0, coordinator.FenceBatchCalls);
        Assert.Empty(coordinator.Registrations);
        Assert.Equal(0, await fixture.Database
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
        var zones = (await transaction.InvokeAsync<IReadOnlyList<TenantDomainZoneSnapshot>>(
            (session, cancellationToken) => capabilities.GetOrCreateGeneratedSetAsync(
                [new GetOrCreateGeneratedZoneRequest(tenantId, targetId, "example.com", 1)],
                session,
                cancellationToken))).AssertSuccess();
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

        var result = await transaction.InvokeAsync<IReadOnlyList<HostnameBindingSnapshot>>(
            (session, cancellationToken) => capabilities.CreateGeneratedSetAsync(
                [valid, invalid],
                session,
                cancellationToken));

        Assert.IsType<ConflictFailure>(result.AssertFailure());
        Assert.Equal(0, await fixture.Database
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
        var zone = Assert.Single((await transaction.InvokeAsync<IReadOnlyList<TenantDomainZoneSnapshot>>(
            (session, cancellationToken) => capabilities.GetOrCreateGeneratedSetAsync(
                [new GetOrCreateGeneratedZoneRequest(tenantId, targetId, "example.com", 1)],
                session,
                cancellationToken))).AssertSuccess());
        var resourceId = ObjectId.GenerateNewId().ToString();
        var deploymentId = ObjectId.GenerateNewId().ToString();
        (await transaction.InvokeAsync<IReadOnlyList<HostnameBindingSnapshot>>(
            (session, cancellationToken) => capabilities.CreateGeneratedSetAsync(
                [CreateBindingRequest(zone, deploymentId, resourceId)],
                session,
                cancellationToken))).AssertSuccess();

        (await transaction.InvokeAsync(
            (session, cancellationToken) => capabilities.DeleteAllForDeploymentAsync(
                tenantId,
                WebServiceType,
                resourceId,
                deploymentId,
                session,
                cancellationToken))).AssertSuccess();

        Assert.Equal(0, await fixture.Database
            .GetCollection<HostnameBinding>("HostnameBindings")
            .CountDocumentsAsync(Builders<HostnameBinding>.Filter.Empty));
        Assert.Equal(0, await fixture.Database
            .GetCollection<TenantDomainZone>("TenantDomainZones")
            .CountDocumentsAsync(Builders<TenantDomainZone>.Filter.Empty));
        var unregistration = Assert.Single(coordinator.Unregistrations);
        Assert.Equal(targetId, unregistration.TargetId);
        Assert.Equal(zone.Id, unregistration.Owner.Id);
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
        var zone = Assert.Single((await transaction.InvokeAsync<IReadOnlyList<TenantDomainZoneSnapshot>>(
            (session, cancellationToken) => capabilities.GetOrCreateGeneratedSetAsync(
                [new GetOrCreateGeneratedZoneRequest(tenantId, targetId, "example.com", 1)],
                session,
                cancellationToken))).AssertSuccess());
        var firstResourceId = ObjectId.GenerateNewId().ToString();
        var secondResourceId = ObjectId.GenerateNewId().ToString();
        var firstDeploymentId = ObjectId.GenerateNewId().ToString();
        var secondDeploymentId = ObjectId.GenerateNewId().ToString();
        (await transaction.InvokeAsync<IReadOnlyList<HostnameBindingSnapshot>>(
            (session, cancellationToken) => capabilities.CreateGeneratedSetAsync(
                [CreateBindingRequest(zone, firstDeploymentId, firstResourceId)],
                session,
                cancellationToken))).AssertSuccess();
        (await transaction.InvokeAsync<IReadOnlyList<HostnameBindingSnapshot>>(
            (session, cancellationToken) => capabilities.CreateGeneratedSetAsync(
                [CreateBindingRequest(zone, secondDeploymentId, secondResourceId)],
                session,
                cancellationToken))).AssertSuccess();

        (await transaction.InvokeAsync(
            (session, cancellationToken) => capabilities.DeleteAllForDeploymentAsync(
                tenantId,
                WebServiceType,
                firstResourceId,
                firstDeploymentId,
                session,
                cancellationToken))).AssertSuccess();

        Assert.Equal(1, await fixture.Database
            .GetCollection<HostnameBinding>("HostnameBindings")
            .CountDocumentsAsync(Builders<HostnameBinding>.Filter.Empty));
        Assert.Equal(1, await fixture.Database
            .GetCollection<TenantDomainZone>("TenantDomainZones")
            .CountDocumentsAsync(Builders<TenantDomainZone>.Filter.Empty));
        var remainingZone = await fixture.Database
            .GetCollection<TenantDomainZone>("TenantDomainZones")
            .Find(item => item.Id == zone.Id)
            .SingleAsync();
        Assert.Equal(4, remainingZone.Revision);
        Assert.Empty(coordinator.Unregistrations);
    }

    private MongoDomainCapabilities CreateCapabilities(
        string tenantId,
        RecordingTargetCoordinator coordinator) =>
        new(
            fixture.Database,
            coordinator,
            coordinator,
            new FixedTenantIdentityLookup(tenantId),
            TimeProvider.System);

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

    private sealed class FixedTenantIdentityLookup(string tenantId) : ITenantIdentityLookup
    {
        public Task<Result<TenantIdentitySnapshot>> GetAsync(
            string requestedTenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<TenantIdentitySnapshot>>(
                string.Equals(requestedTenantId, tenantId, StringComparison.Ordinal)
                    ? new TenantIdentitySnapshot(tenantId, "Acme Corporation")
                    : Failures.NotFound("Tenant not found.", requestedTenantId));
    }

    private sealed class RecordingTargetCoordinator
        : IDeploymentTargetReferenceAdmission,
          IDeploymentTargetWorkloadConfigurationFence
    {
        public int FenceBatchCalls { get; private set; }
        public List<DeploymentTargetWorkloadConfigurationFenceRequest> Fences { get; } = [];
        public List<(string TargetId, DeploymentTargetReferenceOwner Owner)> Registrations { get; } = [];
        public List<(string TargetId, DeploymentTargetReferenceOwner Owner)> Unregistrations { get; } = [];

        public Task<Result> FenceManyAsync(
            IReadOnlyList<DeploymentTargetWorkloadConfigurationFenceRequest> requests,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
        {
            Assert.True(session.IsInTransaction);
            FenceBatchCalls++;
            Fences.AddRange(requests);
            return Task.FromResult<Result>(Result.Success());
        }

        public Task<Result> FenceAsync(
            string deploymentTargetId,
            long expectedWorkloadConfigurationRevision,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default) =>
            throw new Xunit.Sdk.XunitException("The set capability must use the batch target fence.");

        public Task<Result> RegisterAsync(
            string deploymentTargetId,
            DeploymentTargetReferenceOwner owner,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
        {
            Assert.True(session.IsInTransaction);
            Registrations.Add((deploymentTargetId, owner));
            return Task.FromResult<Result>(Result.Success());
        }

        public Task<Result> UnregisterAsync(
            string deploymentTargetId,
            DeploymentTargetReferenceOwner owner,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
        {
            Assert.True(session.IsInTransaction);
            Unregistrations.Add((deploymentTargetId, owner));
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
}
