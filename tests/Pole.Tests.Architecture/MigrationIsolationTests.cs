// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class MigrationIsolationTests
{
    [Fact]
    public void MigrationProjects_OnlyDependOnTheFrozenMigrationApi()
    {
        var root = ArchitectureSources.RepositoryRoot;
        var projects = Directory.EnumerateFiles(
                Path.Combine(root, "src/features"),
                "*.Migrations.*proj",
                SearchOption.AllDirectories)
            .ToArray();
        Assert.NotEmpty(projects);
        foreach (var project in projects)
        {
            var references = XDocument.Load(project)
                .Descendants("ProjectReference")
                .Select(reference => Path.GetFullPath(
                    Path.Combine(
                        Path.GetDirectoryName(project)!,
                        reference.Attribute("Include")!.Value.Replace('\\', '/'))))
                .ToArray();
            Assert.Equal([Path.Combine(root, "src/shared/Migrations/Migrations.csproj")], references);
            Assert.Empty(XDocument.Load(project).Descendants("FrameworkReference"));
            Assert.All(
                XDocument.Load(project).Descendants("PackageReference"),
                reference =>
                {
                    Assert.EndsWith(".fsproj", project);
                    Assert.Equal("FSharp.Core", reference.Attribute("Include")!.Value);
                });
        }

        var shared = XDocument.Load(Path.Combine(root, "src/shared/Migrations/Migrations.csproj"));
        Assert.Empty(shared.Descendants("ProjectReference"));
        Assert.Equal(
            ["MongoDB.Driver"],
            shared.Descendants("PackageReference").Select(reference => reference.Attribute("Include")!.Value));
    }

    [Fact]
    public void FeatureMigrationImplementations_LiveInMigrationProjects()
    {
        var misplaced = ArchitectureSources.ProductionCSharp()
            .Where(source => source.RelativePath.StartsWith("src/features/", StringComparison.Ordinal))
            .Where(source => !source.RelativePath.Contains(".Migrations/", StringComparison.Ordinal))
            .Where(source =>
                source.Root
                    .DescendantNodes()
                    .OfType<BaseTypeSyntax>()
                    .Any(type => type.Type.ToString() is "IMigration" or "Pole.Migrations.IMigration"))
            .Select(source => source.RelativePath);
        var fsharp = ArchitectureSources.ProductionFSharp()
            .Where(source => source.RelativePath.StartsWith("src/features/", StringComparison.Ordinal))
            .Where(source => !source.RelativePath.Contains(".Migrations/", StringComparison.Ordinal))
            .Where(source => source.Text.Contains("interface IMigration", StringComparison.Ordinal))
            .Select(source => source.RelativePath);
        Assert.Empty(misplaced.Concat(fsharp));
    }
}
