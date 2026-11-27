// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using Pole.Dns;
using Pole.Features.Domains.Domain;
using Xunit;

namespace Pole.Tests.Integration.Domains;

public sealed class DnsDomainTests
{
    [Theory]
    [InlineData("API.XN--BCHER-KVA.Example.", "api.xn--bcher-kva.example")]
    [InlineData("service.example", "service.example")]
    public void Dns_name_normalizes_ascii_and_punycode(string input, string expected) =>
        Assert.Equal(expected, DnsName.Create(input).Value);

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("api.example.com:443")]
    [InlineData("*.example.com")]
    [InlineData("127.0.0.1")]
    [InlineData("bücher.example")]
    public void Dns_name_rejects_non_dns_or_unicode_input(string input) =>
        Assert.Throws<ArgumentException>(() => DnsName.Create(input));

    [Fact]
    public void Relative_name_supports_apex_and_nested_names()
    {
        var zone = DnsName.Create("tenant.example.com");

        Assert.Equal("tenant.example.com", RelativeDnsName.Create("@").Under(zone).Value);
        Assert.Equal("api.eu.tenant.example.com", RelativeDnsName.Create("API.EU").Under(zone).Value);
    }

    [Fact]
    public void Zone_and_binding_factories_snapshot_normalized_dns_and_explicit_timestamps()
    {
        var now = DateTime.UtcNow;
        var tenantId = ObjectId.GenerateNewId().ToString();
        var targetId = ObjectId.GenerateNewId().ToString();
        var zone = TenantDomainZoneFactory.CreateManagedReservation(
            tenantId,
            targetId,
            "Company",
            DnsName.Create("Example.COM."),
            "zone-" + ObjectId.GenerateNewId(),
            new TenantDomainZoneFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), now));
        var binding = HostnameBindingFactory.CreateCustom(
            tenantId,
            zone.Id,
            targetId,
            ResourceTypeId.Create("pole/web-service"),
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            "Api.Eu",
            DnsName.Create(zone.Domain),
            "binding-" + ObjectId.GenerateNewId(),
            new HostnameBindingFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), now));

        Assert.Equal("company.example.com", zone.Domain);
        Assert.Equal("api.eu.company.example.com", binding.Hostname);
        Assert.Equal(now, zone.CreatedAtUtc);
        Assert.Equal(now, binding.CreatedAtUtc);
    }

    [Fact]
    public void Managed_zone_cannot_use_platform_generated_label_namespace()
    {
        var exception = Assert.Throws<ArgumentException>(() => TenantDomainZoneFactory.CreateManagedReservation(
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            $"{TenantDomainZoneLabelPolicy.GeneratedPrefix}acme-12345678",
            DnsName.Create("example.com"),
            "zone-" + ObjectId.GenerateNewId(),
            new TenantDomainZoneFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow)));

        Assert.Contains("reserved", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
