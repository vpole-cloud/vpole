using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Dns;
using Pole.Features.Domains.Application.Persistence;
using Pole.Features.Domains.Application.Persistence.DomainZones;
using Pole.Features.Domains.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Domains;

[Collection(IntegrationTestCollection.Name)]
public sealed class DomainZoneLoadCapabilityTests(MongoReplicaSetFixture fixture)
    : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Loaders_preserve_the_tenant_boundary_for_zone_id_and_target_queries()
    {
        var tenantId = ObjectId.GenerateNewId().ToString();
        var otherTenantId = ObjectId.GenerateNewId().ToString();
        var deploymentTargetId = ObjectId.GenerateNewId().ToString();
        var otherDeploymentTargetId = ObjectId.GenerateNewId().ToString();
        var zone = CreateZone(tenantId, deploymentTargetId, "api");
        var otherTargetZone = CreateZone(tenantId, otherDeploymentTargetId, "internal");
        var foreignZone = CreateZone(otherTenantId, deploymentTargetId, "foreign");
        await fixture.Database
            .GetCollection<TenantDomainZone>(DomainCollectionNames.TenantDomainZones)
            .InsertManyAsync([zone, otherTargetZone, foreignZone]);
        var loadZone = new MongoLoadTenantDomainZone(fixture.Database);
        var loadZones = new MongoLoadTenantDomainZones(fixture.Database);

        var foreignLookup = await loadZone.InvokeAsync(tenantId, foreignZone.Id);
        var selected = await loadZones.InvokeAsync(tenantId, deploymentTargetId);

        var notFound = Assert.IsType<DomainZoneNotFound>(foreignLookup.Value);
        Assert.Equal(foreignZone.Id, notFound.ZoneId);
        var snapshot = Assert.Single(selected);
        Assert.Equal(zone.Id, snapshot.Id);
        Assert.Equal(tenantId, snapshot.TenantId);
        Assert.Equal(deploymentTargetId, snapshot.DeploymentTargetId);
    }

    private static TenantDomainZone CreateZone(
        string tenantId,
        string deploymentTargetId,
        string label) =>
        TenantDomainZoneFactory.CreateManagedReservation(
            tenantId,
            deploymentTargetId,
            label,
            DnsName.Create("apps.example.test"),
            new TenantDomainZoneFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                DateTime.UtcNow));
}
