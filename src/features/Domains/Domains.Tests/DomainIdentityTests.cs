// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Dns;
using Pole.Features.Domains.Domain;
using Pole.Workloads;
using Xunit;

namespace Pole.Features.Domains.Tests;

public sealed class DomainIdentityTests
{
    [Fact]
    public void Managed_zone_and_custom_binding_keep_names_separate_from_dns()
    {
        var zone = ManagedZone("tenant-zone");
        var binding = HostnameBindingFactory.CreateCustom(
            zone.TenantId,
            zone.Id,
            zone.DeploymentTargetId,
            ResourceTypeId.Create("pole/web-service"),
            Id(),
            Id(),
            "@",
            DnsName.Create(zone.Domain),
            "website-alias",
            new HostnameBindingFactory.CreateOptions(Id(), new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc)));

        Assert.Equal("tenant-zone", Assert.IsType<NamedDomainIdentity>(zone.Identity).TechnicalName);
        Assert.Equal("website-alias", Assert.IsType<NamedDomainIdentity>(binding.Identity).TechnicalName);
        Assert.Equal("company.example.test", zone.Domain);
        Assert.Equal(zone.Domain, binding.Hostname);
        Assert.Equal(1, zone.Revision);
        Assert.Equal(0, zone.ReferenceFence);
        Assert.Equal(0, binding.ReferenceFence);
        Assert.False(zone.ToBsonDocument().Contains("Kind"));
        Assert.False(binding.ToBsonDocument().Contains("Kind"));
        Assert.Equal(zone, BsonSerializer.Deserialize<TenantDomainZone>(zone.ToBson()));
        Assert.Equal(binding, BsonSerializer.Deserialize<HostnameBinding>(binding.ToBson()));
    }

    [Fact]
    public void Generated_entities_have_system_identity_without_a_user_name()
    {
        var zone = TenantDomainZoneFactory.CreateGenerated(
            Id(),
            Id(),
            "pole-generated-tenant",
            DnsName.Create("example.test"),
            new TenantDomainZoneFactory.CreateOptions(Id(), new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc)));
        var binding = HostnameBindingFactory.CreateGenerated(
            zone.TenantId,
            zone.Id,
            zone.DeploymentTargetId,
            ResourceTypeId.Create("pole/web-service"),
            Id(),
            Id(),
            "app",
            DnsName.Create(zone.Domain),
            new HostnameBindingFactory.CreateOptions(Id(), new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc)));

        Assert.IsType<GeneratedDomainIdentity>(zone.Identity);
        Assert.IsType<GeneratedDomainIdentity>(binding.Identity);
        Assert.False(zone.ToBsonDocument()["Identity"].AsBsonDocument.Contains("TechnicalName"));
        Assert.Equal(zone, BsonSerializer.Deserialize<TenantDomainZone>(zone.ToBson()));
        Assert.Equal(binding, BsonSerializer.Deserialize<HostnameBinding>(binding.ToBson()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Upper")]
    [InlineData("1zone")]
    [InlineData("has.dot")]
    [InlineData("ends-")]
    public void Managed_identity_rejects_invalid_technical_names(string name) =>
        Assert.Throws<ArgumentException>(() => ManagedZone(name));

    private static TenantDomainZone ManagedZone(string name) => TenantDomainZoneFactory.CreateManagedReservation(
        Id(),
        Id(),
        "company",
        DnsName.Create("example.test"),
        name,
        new TenantDomainZoneFactory.CreateOptions(Id(), new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc)));

    private static string Id() => ObjectId.GenerateNewId().ToString();
}
