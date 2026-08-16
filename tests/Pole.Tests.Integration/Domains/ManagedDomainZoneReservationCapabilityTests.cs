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

        Assert.True(result.TryGetValue(out var zone, out _));
        Assert.Equal("api.apps.example.test", zone.Domain);
        Assert.Equal(
            1L,
            await fixture.Database
                .GetCollection<TenantDomainZone>("TenantDomainZones")
                .CountDocumentsAsync(Builders<TenantDomainZone>.Filter.Empty));
        Assert.Equal(1, coordinator.RegistrationCalls);
    }

    [Fact]
    public async Task TypedOutcome_AbortsDuplicateReservation()
    {
        var coordinator = new TargetCoordinator();
        var tenantId = ObjectId.GenerateNewId().ToString();
        var targetId = ObjectId.GenerateNewId().ToString();
        var first = await CreateReservationAsync(coordinator, tenantId, targetId);
        Assert.True(first.TryGetValue(out _, out _));

        var duplicate = await CreateReservationAsync(coordinator, tenantId, targetId);

        Assert.False(duplicate.TryGetValue(out _, out var error));
        var conflict = error switch
        {
            ManagedDomainZoneAlreadyReserved value => value,
            _ => throw new Xunit.Sdk.XunitException(
                $"Expected {nameof(ManagedDomainZoneAlreadyReserved)}.")
        };
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

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.True(error is ManagedDomainZoneReferenceRejected);
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

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.True(error is ManagedDomainZoneDeploymentTargetChanged);
        Assert.Equal(
            0L,
            await fixture.Database
                .GetCollection<TenantDomainZone>("TenantDomainZones")
                .CountDocumentsAsync(Builders<TenantDomainZone>.Filter.Empty));
        Assert.Equal(0, coordinator.RegistrationCalls);
    }

    private async Task<Result<TenantDomainZoneSnapshot, CreateManagedDomainZoneError>>
        CreateReservationAsync(
            TargetCoordinator coordinator,
            string? tenantId = null,
            string? targetId = null)
    {
        var persistence = new MongoDomainPersistence(
            fixture.Database,
            coordinator,
            coordinator,
            new UnusedTenantIdentityLookup(),
            TimeProvider.System);
        var transaction = new MongoRunTransaction(fixture.Client);
        tenantId ??= ObjectId.GenerateNewId().ToString();
        targetId ??= ObjectId.GenerateNewId().ToString();

        return await transaction.InvokeOutcomeAsync<Result<
            TenantDomainZoneSnapshot,
            CreateManagedDomainZoneError>>(
            async (context, cancellationToken) =>
            {
                var result = await persistence.CreateManagedReservationOutcomeAsync(
                    tenantId,
                    targetId,
                    "api",
                    "apps.example.test",
                    expectedWorkloadConfigurationRevision: 1,
                    context.Session,
                    cancellationToken);

                return result.TryGetValue(out _, out _)
                    ? context.Commit(result)
                    : context.Abort(result);
            });
    }

    private sealed class TargetCoordinator
        : IDeploymentTargetReferenceAdmission,
          IDeploymentTargetWorkloadConfigurationFence
    {
        public bool RejectFence { get; init; }
        public bool RejectReference { get; init; }
        public int RegistrationCalls { get; private set; }

        public Task<Result> RegisterAsync(
            string deploymentTargetId,
            DeploymentTargetReferenceOwner owner,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
        {
            RegistrationCalls++;
            return Task.FromResult<Result>(RejectReference
                ? Failures.Conflict("Reference rejected for the test.")
                : Result.Success());
        }

        public Task<Result> UnregisterAsync(
            string deploymentTargetId,
            DeploymentTargetReferenceOwner owner,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result>(Result.Success());

        public Task<Result> ApplyChangesAsync(
            DeploymentTargetReferenceOwner owner,
            IReadOnlyCollection<string> addedDeploymentTargetIds,
            IReadOnlyCollection<string> removedDeploymentTargetIds,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result>(Result.Success());

        public Task<Result> FenceAsync(
            string deploymentTargetId,
            long expectedWorkloadConfigurationRevision,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result>(RejectFence
                ? Failures.Conflict("Fence rejected for the test.")
                : Result.Success());

        public Task<Result> FenceManyAsync(
            IReadOnlyList<DeploymentTargetWorkloadConfigurationFenceRequest> requests,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result>(Result.Success());
    }

    private sealed class UnusedTenantIdentityLookup : ITenantIdentityLookup
    {
        public Task<Result<TenantIdentitySnapshot>> GetAsync(
            string tenantId,
            CancellationToken cancellationToken = default) =>
            throw new Xunit.Sdk.XunitException(
                "Managed reservation creation must not resolve tenant identity.");
    }
}
