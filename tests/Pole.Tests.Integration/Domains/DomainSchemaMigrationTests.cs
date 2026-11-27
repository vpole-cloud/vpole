// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Dns;
using Pole.Features.Domains.Domain;
using Pole.Features.Domains.Migrations;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Domains;

[Collection(IntegrationTestCollection.Global2)]
public sealed class DomainSchemaMigrationTests(
    MongoReplicaSetFixture fixture
) : MongoIntegrationTestBase(fixture)
{
    public override Task InitializeAsync() => base.InitializeAsync();
    public override Task DisposeAsync() => base.DisposeAsync();

    [Fact]
    public async Task Migrations_create_strict_collections_and_required_indexes()
    {
        await new TenantDomainZoneSchemaValidationMigration().Up(Fixture.Database);
        await new HostnameBindingSchemaValidationMigration().Up(Fixture.Database);
        await new DomainIndexesMigration().Up(Fixture.Database);

        await new MongoDocumentContractTester<BsonDocument>(Fixture.Database, "TenantDomainZones")
            .AssertStrictErrorValidationAsync();
        await new MongoDocumentContractTester<BsonDocument>(Fixture.Database, "HostnameBindings")
            .AssertStrictErrorValidationAsync();

        var now = DateTime.UtcNow;
        var tenantId = ObjectId.GenerateNewId().ToString();
        var targetId = ObjectId.GenerateNewId().ToString();
        var zone = TenantDomainZoneFactory.CreateManagedReservation(
            tenantId,
            targetId,
            "company",
            DnsName.Create("example.com"),
            "zone-" + ObjectId.GenerateNewId(),
            new TenantDomainZoneFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), now));
        var binding = HostnameBindingFactory.CreateCustom(
            tenantId,
            zone.Id,
            targetId,
            ResourceTypeId.Create("pole/web-service"),
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            "api",
            DnsName.Create(zone.Domain),
            "binding-" + ObjectId.GenerateNewId(),
            new HostnameBindingFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), now));
        await new MongoDocumentContractTester<TenantDomainZone>(Fixture.Database, "TenantDomainZones")
            .AssertRejectsAsync(zone, document => document["Unexpected"] = true);
        await new MongoDocumentContractTester<HostnameBinding>(Fixture.Database, "HostnameBindings").AssertRejectsAsync(
            binding,
            document => document["Unexpected"] = true);

        var zoneIndexes
            = (await Fixture.Database.GetCollection<BsonDocument>("TenantDomainZones").Indexes.List().ToListAsync())
            .Select(index => index["name"].AsString)
            .ToHashSet(StringComparer.Ordinal);
        var bindingIndexes
            = (await Fixture.Database.GetCollection<BsonDocument>("HostnameBindings").Indexes.List().ToListAsync())
            .Select(index => index["name"].AsString)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("UX_TenantDomainZones_Target_Domain", zoneIndexes);
        Assert.Contains("UX_TenantDomainZones_Tenant_Target_Generated", zoneIndexes);
        Assert.Contains("UX_HostnameBindings_Hostname", bindingIndexes);
        Assert.Contains("UX_HostnameBindings_Tenant_Deployment_Generated", bindingIndexes);
        Assert.Contains("IX_HostnameBindings_Zone", bindingIndexes);
    }

    [Fact]
    public async Task Indexes_freeze_scalar_identity_discriminators_and_compound_key_order()
    {
        await new TenantDomainZoneSchemaValidationMigration().Up(Fixture.Database);
        await new HostnameBindingSchemaValidationMigration().Up(Fixture.Database);
        await new DomainIndexesMigration().Up(Fixture.Database);

        foreach (var (collection, generatedIndex, parentField) in new[]
                 {
                     ("TenantDomainZones", "UX_TenantDomainZones_Tenant_Target_Generated", "DeploymentTargetId"),
                     ("HostnameBindings", "UX_HostnameBindings_Tenant_Deployment_Generated", "ResourceDeploymentId"),
                 })
        {
            var indexes = await Fixture.Database.GetCollection<BsonDocument>(collection).Indexes.List().ToListAsync();
            var generated = Assert.Single(indexes, index => index["name"] == generatedIndex);
            Assert.Equal(
                new BsonDocument { ["TenantId"] = 1, [parentField] = 1, ["Identity._t"] = 1 },
                generated["key"]);
            Assert.True(generated["unique"].AsBoolean);
            Assert.Equal(
                new BsonDocument("Identity._t", "GeneratedDomainIdentity"),
                generated["partialFilterExpression"]);
            var named = Assert.Single(indexes, index => index["name"] == $"UX_{collection}_Tenant_TechnicalName");
            Assert.Equal(new BsonDocument { ["TenantId"] = 1, ["Identity.TechnicalName"] = 1 }, named["key"]);
            Assert.Equal(new BsonDocument("Identity._t", "NamedDomainIdentity"), named["partialFilterExpression"]);
        }
    }

    [Fact]
    public async Task Schema_migrations_upgrade_existing_collections_with_collmod()
    {
        await Fixture.Database.CreateCollectionAsync("TenantDomainZones");
        await Fixture.Database.CreateCollectionAsync("HostnameBindings");

        await new TenantDomainZoneSchemaValidationMigration().Up(Fixture.Database);
        await new HostnameBindingSchemaValidationMigration().Up(Fixture.Database);

        await new MongoDocumentContractTester<BsonDocument>(Fixture.Database, "TenantDomainZones")
            .AssertStrictErrorValidationAsync();
        await new MongoDocumentContractTester<BsonDocument>(Fixture.Database, "HostnameBindings")
            .AssertStrictErrorValidationAsync();
    }
}
