// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Nodes;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Application.Declarations;
using Xunit;

namespace Pole.Features.Postgres.Tests;

public sealed class PostgresDeclarationTests
{
    private static JsonObject Spec() => JsonNode.Parse(
        """
        {
          "resourceGroup":"data",
          "placement":{"deploymentTarget":"lab","computeProfile":{"name":"small","revision":2}},
          "storage":{"sizeGb":10,"profile":{"name":"persistent","revision":3}},
          "version":16,
          "bootstrapPassword":{"secret":"database-password","key":"password"}
        }
        """)!.AsObject();

    [Fact]
    public void NormalizationMakesInitialCapacityExplicitAndRetainsOnlyBootstrapReference()
    {
        var normalized = PostgresDeclarations.normalize(
            new PostgresSettings(),
            JsonSerializer.SerializeToElement(Spec()));
        Assert.Equal(1, normalized.GetProperty("instances").GetInt32());
        Assert.Equal(
            normalized.GetRawText(),
            PostgresDeclarations.normalize(new PostgresSettings(), normalized).GetRawText());
        var facts = PostgresDeclarations.facts(normalized);
        Assert.Contains(
            facts.References,
            item => item.Target == new ResourceAddress("Secret", "database-password") &&
                item.RequiredKey == "password" && item.RequiresAccess);
        var steps = PostgresDeclarations.steps(new ResourceAddress("PostgresCluster", "database"), normalized);
        Assert.Equal([DeclarationPhase.Prepare, DeclarationPhase.Configure], steps.Select(item => item.Phase));
    }

    [Theory]
    [InlineData("password", "private-value")]
    [InlineData("username", "application")]
    [InlineData("tags", "not-managed")]
    public void RejectsInlineCredentialsAndServerOwnedOrUndeclaredFields(string field, string value)
    {
        var spec = Spec();
        spec[field] = value;
        Assert.Throws<JsonException>(() => PostgresDeclarations.normalize(
            new PostgresSettings(),
            JsonSerializer.SerializeToElement(spec)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsNonpositiveInstances(int count)
    {
        var spec = Spec();
        spec["instances"] = count;
        Assert.Throws<DeclarationSchemaException>(() =>
            PostgresDeclarations.normalize(new PostgresSettings(), JsonSerializer.SerializeToElement(spec)));
    }
}
