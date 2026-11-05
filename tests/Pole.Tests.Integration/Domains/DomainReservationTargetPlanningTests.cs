using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Application.Admission;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Tests.Integration.DeploymentTargets;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Domains;

[Collection(IntegrationTestCollection.Name)]
public sealed class DomainReservationTargetPlanningTests(MongoReplicaSetFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Reservation_planning_pins_target_configuration_and_fences_without_business_changes()
    {
        var target = PublicTarget();
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        var provider = new MongoDomainReservationTargetPlanning(fixture.DbContext.DeploymentTargets);
        using var session = await fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var snapshot = Assert.IsType<DomainReservationTargetPlanningSnapshot>((await provider.ReadAsync(
            new(TestData.TenantId, target.Id), session, default)).Value);
        Assert.IsType<DomainReservationTargetPlanningFenced>(
            (await provider.FenceAsync(snapshot, session, default)).Value);
        await session.CommitTransactionAsync();
        var stored = await fixture.DbContext.DeploymentTargets.Find(item => item.Id == target.Id).SingleAsync();
        Assert.Equal(target.Revision, stored.Revision);
        Assert.Equal(target.WorkloadConfiguration.Revision, stored.WorkloadConfiguration.Revision);
        Assert.Equal(target.ReferenceFence + 1, stored.ReferenceFence);

        await fixture.DbContext.DeploymentTargets.ReplaceOneAsync(item => item.Id == target.Id,
            stored.SetEnabled(false, DateTime.UtcNow));
        session.StartTransaction();
        Assert.IsType<DomainReservationTargetPlanningChanged>(
            (await provider.FenceAsync(snapshot, session, default)).Value);
        Assert.IsType<DomainReservationTargetPlanningUnavailable>((await provider.ReadAsync(
            snapshot.Request, session, default)).Value);
        await session.AbortTransactionAsync();
    }

    [Fact]
    public async Task Reservation_requires_public_ingress_and_rollback_keeps_reference_fence()
    {
        var target = PublicTarget();
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        var provider = new MongoDomainReservationTargetPlanning(fixture.DbContext.DeploymentTargets);
        using var session = await fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var snapshot = Assert.IsType<DomainReservationTargetPlanningSnapshot>((await provider.ReadAsync(
            new(TestData.TenantId, target.Id), session, default)).Value);
        await provider.FenceAsync(snapshot, session, default);
        await session.AbortTransactionAsync();
        Assert.Equal(target.ReferenceFence,
            (await fixture.DbContext.DeploymentTargets.Find(item => item.Id == target.Id).SingleAsync())
            .ReferenceFence);
        await fixture.DbContext.DeploymentTargets.ReplaceOneAsync(item => item.Id == target.Id,
            target with { WorkloadConfiguration = target.WorkloadConfiguration with { Ingress = null } });
        session.StartTransaction();
        var unavailable = Assert.IsType<DomainReservationTargetPlanningUnavailable>((await provider.ReadAsync(
            snapshot.Request, session, default)).Value);
        Assert.IsType<DeploymentTargetPublicIngressUnavailable>(unavailable.Error.Value);
        await session.AbortTransactionAsync();
    }

    private static DeploymentTarget PublicTarget() =>
        (TestData.SeedDeploymentTarget(ObjectId.GenerateNewId().ToString())
        with
        {
            WorkloadConfiguration = DeploymentTargetWorkloadConfiguration.Create(
                DeploymentTargetIngressConfiguration.Create("traefik", "issuer", "apps.example.test"),
                new(ImmutableDictionary<string, DeploymentTargetNetworkSource>.Empty.Add("public-ingress",
                    DeploymentTargetCidrNetworkSource.Create("192.0.2.0/24"))))
        }).AttestForTests();
}
