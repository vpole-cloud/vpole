// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.Domains.Application.DomainZones;
using Pole.Features.Domains.Domain;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Xunit;

namespace Pole.Features.Domains.Tests;

public sealed class DomainZoneDeclarationTests
{
    private static readonly IMongoDatabase Database
        = new MongoClient("mongodb://localhost:27017").GetDatabase("domain-schema-tests");

    private static DeclarativeDomainZoneProvider Provider() => new(
        Database.GetCollection<TenantDomainZone>("TenantDomainZones"),
        null!,
        null!,
        null!,
        null!,
        null!,
        null!,
        null!,
        null!);

    [Fact]
    public void Zone_normalizes_dns_label_but_keeps_technical_target_identity()
    {
        var provider = Provider();
        var normalized = provider.NormalizeAndValidate(
            DeclarationJson.Write(
                new
                {
                    deploymentTarget = "public-target",
                    label = "Company",
                }));
        var declaration = DeclarationJson.Read<DomainZoneDeclaration>(normalized);
        Assert.Equal("company", declaration.Label);
        Assert.Equal("public-target", declaration.DeploymentTarget);
        var step = Assert.Single(provider.Steps(new ResourceAddress("DomainZone", "company-zone"), normalized));
        Assert.Equal(DeclarationPhase.Prepare, step.Phase);
        Assert.Empty(step.Dependencies);
    }

    [Theory]
    [InlineData("Upper", "company", "spec.deploymentTarget")]
    [InlineData("1target", "company", "spec.deploymentTarget")]
    [InlineData("public", "invalid.label", "spec.label")]
    [InlineData("public", "pole-generated-system", "spec.label")]
    public void Zone_rejects_invalid_or_reserved_identity(
        string target,
        string label,
        string field)
    {
        var error = Assert.Throws<DeclarationSchemaException>(() =>
            Provider().NormalizeAndValidate(DeclarationJson.Write(new { deploymentTarget = target, label })));
        Assert.Equal(field, error.Field);
    }

    [Fact]
    public void Zone_rejects_unknown_fields() => Assert.Throws<JsonException>(() => Provider()
        .NormalizeAndValidate(
            DeclarationJson.Write(
                new
                {
                    deploymentTarget = "public",
                    label = "company",
                    credentials = "not-supported",
                })));
}
