// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using Pole.Dns;
using Pole.Features.Domains.Application.DomainZones;
using Pole.Features.Domains.Application.Mongo;
using Pole.Features.Domains.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Domains;

[Collection(IntegrationTestCollection.Global3)]
public sealed class DomainZoneLoadCapabilityTests(
    MongoReplicaSetFixture fixture
) : MongoIntegrationTestBase(fixture)
{
    public override Task InitializeAsync() => base.InitializeAsync();

    public override Task DisposeAsync() => base.DisposeAsync();

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
        await Fixture.Database
            .GetCollection<TenantDomainZone>(DomainCollectionNames.TenantDomainZones)
            .InsertManyAsync([zone, otherTargetZone, foreignZone]);
        var zones = Fixture.Database.GetCollection<TenantDomainZone>(DomainCollectionNames.TenantDomainZones);
        var loadZone = new MongoLoadTenantDomainZone(zones);
        var loadZones = new MongoLoadTenantDomainZones(zones);

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
        string label) => TenantDomainZoneFactory.CreateManagedReservation(
        tenantId,
        deploymentTargetId,
        label,
        DnsName.Create("apps.example.test"),
        "zone-" + ObjectId.GenerateNewId(),
        new TenantDomainZoneFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow));
}
