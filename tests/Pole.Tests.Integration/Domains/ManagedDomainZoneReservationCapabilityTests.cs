using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Application.Migrations;
using Pole.Features.Domains.Application.Persistence;
using Pole.Features.Domains.Application.Persistence.DomainZones;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Domains;

[Collection(IntegrationTestCollection.Name)]
public sealed class ManagedDomainZoneReservationCapabilityTests(
    MongoReplicaSetFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await fixture.ResetDatabaseAsync();
        await new TenantDomainZoneSchemaValidationMigration().Up(fixture.Database);
        await new DomainIndexesMigration().Up(fixture.Database);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task TypedOutcome_CommitsCreatedReservation()
    {
        var coordinator = new TargetCoordinator();
        var result = await CreateReservationAsync(coordinator);

        var zone = Assert.IsType<TenantDomainZoneSnapshot>(result.Value);
        Assert.Equal("api.apps.example.test", zone.Domain);
        Assert.Equal(
            1L,
            await fixture.Database
                .GetCollection<TenantDomainZone>("TenantDomainZones")
                .CountDocumentsAsync(Builders<TenantDomainZone>.Filter.Empty));
        Assert.Equal(1, coordinator.RegistrationCalls);
        Assert.Equal(1, await fixture.DbContext.CountOutboxMessagesAsync("pole.domains/v1/domain-metadata-changed"));
    }

    [Fact]
    public async Task TypedOutcome_AbortsDuplicateReservation()
    {
        var coordinator = new TargetCoordinator();
        var tenantId = ObjectId.GenerateNewId().ToString();
        var targetId = ObjectId.GenerateNewId().ToString();
        var first = await CreateReservationAsync(coordinator, tenantId, targetId);
        Assert.IsType<TenantDomainZoneSnapshot>(first.Value);

        var duplicate = await CreateReservationAsync(coordinator, tenantId, targetId);

        var conflict = Assert.IsType<ManagedDomainZoneAlreadyReserved>(duplicate.Value);
        Assert.Equal(1, await fixture.DbContext.CountOutboxMessagesAsync("pole.domains/v1/domain-metadata-changed"));
        Assert.Equal("api.apps.example.test", conflict.Domain);
        Assert.Equal(
            1L,
            await fixture.Database
                .GetCollection<TenantDomainZone>("TenantDomainZones")
                .CountDocumentsAsync(Builders<TenantDomainZone>.Filter.Empty));
    }

    [Fact]
    public async Task TypedOutcome_AbortsZoneWriteWhenReferenceIsRejected()
    {
        var coordinator = new TargetCoordinator { RejectReference = true };

        var result = await CreateReservationAsync(coordinator);

        Assert.IsType<ManagedDomainZoneReferenceRejected>(result.Value);
        Assert.Equal(0, await fixture.DbContext.CountOutboxMessagesAsync("pole.domains/v1/domain-metadata-changed"));
        Assert.Equal(
            0L,
            await fixture.Database
                .GetCollection<TenantDomainZone>("TenantDomainZones")
                .CountDocumentsAsync(Builders<TenantDomainZone>.Filter.Empty));
        Assert.Equal(1, coordinator.RegistrationCalls);
    }

    [Fact]
    public async Task TypedOutcome_DoesNotWriteWhenTargetFenceIsRejected()
    {
        var coordinator = new TargetCoordinator { RejectFence = true };

        var result = await CreateReservationAsync(coordinator);

        Assert.IsType<ManagedDomainZoneDeploymentTargetChanged>(result.Value);
        Assert.Equal(0, await fixture.DbContext.CountOutboxMessagesAsync("pole.domains/v1/domain-metadata-changed"));
        Assert.Equal(
            0L,
            await fixture.Database
                .GetCollection<TenantDomainZone>("TenantDomainZones")
                .CountDocumentsAsync(Builders<TenantDomainZone>.Filter.Empty));
        Assert.Equal(0, coordinator.RegistrationCalls);
    }

    [Fact]
    public async Task Structural_input_throws_instead_of_becoming_a_typed_error()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => CreateReservationAsync(
            new TargetCoordinator(),
            label: "invalid.label"));
    }

    private async Task<CreateManagedDomainZoneOutcome>
        CreateReservationAsync(
            TargetCoordinator coordinator,
            string? tenantId = null,
            string? targetId = null,
            string label = "api")
    {
        var reservation = new MongoCreateManagedDomainZoneReservation(
            fixture.Database,
            coordinator.ApplyChangesOutcomeAsync,
            coordinator.FenceManyOutcomeAsync,
            TimeProvider.System,
            fixture.StoreOutboxWrites);
        var transaction = new MongoRunTransaction(fixture.Client);
        tenantId ??= ObjectId.GenerateNewId().ToString();
        targetId ??= ObjectId.GenerateNewId().ToString();

        return await
            transaction.InvokeOutcomeAsync<CreateManagedDomainZoneOutcome>(async (context, cancellationToken) =>
            {
                var result = await reservation.InvokeAsync(
                    tenantId,
                    targetId,
                    label,
                    "apps.example.test",
                    expectedWorkloadConfigurationRevision: 1,
                    context.Session,
                    cancellationToken);

                return result is TenantDomainZoneSnapshot
                    ? context.Commit(result)
                    : context.Abort(result);
            });
    }

    private sealed class TargetCoordinator
    {
        public bool RejectFence { get; init; }

        public bool RejectReference { get; init; }

        public int RegistrationCalls { get; private set; }

        public Task<ApplyDeploymentTargetReferenceChangesOutcome> ApplyChangesOutcomeAsync(
            ApplyDeploymentTargetReferenceChangesRequest request,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
        {
            RegistrationCalls += request.AddedDeploymentTargetIds.Count;
            return Task.FromResult<ApplyDeploymentTargetReferenceChangesOutcome>(
                RejectReference
                    ? new ApplyDeploymentTargetReferenceChangesOutcome(
                        new DeploymentTargetReferenceChangeRejected(
                            request.Owner,
                            request.AddedDeploymentTargetIds.Concat(request.RemovedDeploymentTargetIds).ToArray()))
                    : new DeploymentTargetReferencesChanged());
        }

        public Task<FenceDeploymentTargetWorkloadConfigurationsOutcome> FenceManyOutcomeAsync(
            IReadOnlyList<DeploymentTargetWorkloadConfigurationFenceRequest> requests,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<FenceDeploymentTargetWorkloadConfigurationsOutcome>(
                RejectFence
                    ? new FenceDeploymentTargetWorkloadConfigurationsOutcome(
                        new DeploymentTargetWorkloadConfigurationFenceRejected(requests))
                    : new DeploymentTargetWorkloadConfigurationsFenced());
    }
}
