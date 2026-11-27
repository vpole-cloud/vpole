// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class PersistedFeatureModelOwnershipTests
{
    [Fact]
    public void FSharp_collection_roots_belong_to_the_owning_domain_project()
    {
        var models = ArchitectureSources.ProductionCSharp()
            .Where(source => source.RelativePath.StartsWith("src/features/", StringComparison.Ordinal))
            .SelectMany(source =>
                source.Root
                    .DescendantNodes()
                    .OfType<BaseTypeDeclarationSyntax>()
                    .Select(type => new Model(
                        type.Identifier.ValueText,
                        string.Join(
                            ".",
                            type.Ancestors()
                                .OfType<BaseNamespaceDeclarationSyntax>()
                                .Reverse()
                                .Select(ns => ns.Name.ToString())),
                        source.RelativePath)))
            .Concat(
                ArchitectureSources.ProductionFSharp()
                    .Where(source => source.RelativePath.StartsWith("src/features/", StringComparison.Ordinal))
                    .SelectMany(source =>
                        Regex.Matches(source.Text, @"(?m)^\s*type\s+(?:(?:internal|private|public)\s+)?(?<name>\w+)")
                            .Select(match => new Model(
                                match.Groups["name"].Value,
                                NamespaceOf(source.Text),
                                source.RelativePath))))
            .ToArray();

        var violations = ArchitectureSources.ProductionFSharp().SelectMany(source => Violations(source, models));

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("Pole.Features.Sample.Domain", "src/features/Sample/Sample.Domain/Row.fs", false)]
    [InlineData("Pole.Features.Sample.Application", "src/features/Sample/Sample.Application/Row.fs", true)]
    [InlineData("Pole.Features.Sample.Domain", "src/features/Sample/Sample.Application/Row.fs", true)]
    [InlineData("Pole.Features.Sample.Domainish", "src/features/Sample/Sample.Domain/Row.fs", true)]
    public void FSharp_guard_checks_resolved_model_ownership_and_physical_project(
        string modelNamespace,
        string modelPath,
        bool rejected)
    {
        var source = new ArchitectureTextSource(
            "src/features/Sample/Sample.Application/Persistence/Store.fs",
            $"namespace Store\nopen {modelNamespace}\nlet rows = db.GetCollection<Row>(\"rows\")");
        var violations = Violations(source, [new Model("Row", modelNamespace, modelPath)]);

        Assert.Equal(rejected, violations.Any());
    }

    private static IEnumerable<string> Violations(ArchitectureTextSource source, IReadOnlyCollection<Model> models)
    {
        var opened = Regex.Matches(source.Text, @"(?m)^\s*open\s+(?<name>[\w.]+)")
            .Select(match => match.Groups["name"].Value)
            .Append(NamespaceOf(source.Text))
            .ToHashSet();
        foreach (Match usage in Regex.Matches(
                     source.Text,
                     @"(?:GetCollection|IMongoCollection)\s*<\s*(?<model>[\w.]+)\s*>"))
        {
            var name = usage.Groups["model"].Value;
            var candidates = models.Where(model => name == model.Namespace + "." + model.Name ||
                (name == model.Name && opened.Contains(model.Namespace)));
            foreach (var model in candidates)
            {
                var parts = model.Path.Split('/');
                if (parts.Length < 4 || parts[0] != "src" || parts[1] != "features")
                    continue;

                var expected = "Pole.Features." + parts[2] + ".Domain";
                if (parts[3] == parts[2] + ".Domain" && (model.Namespace == expected ||
                        model.Namespace.StartsWith(expected + ".", StringComparison.Ordinal)))
                    continue;

                yield return source.RelativePath + ": persisted model " + model.Namespace + "." + model.Name +
                    " must belong to its owning Domain project.";
            }
        }
    }

    private static string NamespaceOf(string text) =>
        Regex.Match(text, @"(?m)^\s*namespace\s+(?<name>[\w.]+)").Groups["name"].Value;

    private sealed record Model(
        string Name,
        string Namespace,
        string Path
    );
}
