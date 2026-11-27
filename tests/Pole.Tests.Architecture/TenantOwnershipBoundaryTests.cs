// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Xml.Linq;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class TenantOwnershipBoundaryTests
{
    [Fact]
    public void Ownership_primitives_do_not_depend_on_runtime_or_persistence_projects()
    {
        var project = XDocument.Load(
            Path.Combine(
                ArchitectureSources.RepositoryRoot,
                "src/shared/Multitenancy.Primitives/Multitenancy.Primitives.csproj"));

        Assert.Empty(project.Descendants("ProjectReference"));
        Assert.Empty(project.Descendants("PackageReference"));
    }

    [Fact]
    public void Domain_ownership_depends_on_primitives_instead_of_execution_context()
    {
        var root = ArchitectureSources.RepositoryRoot;
        var primitives = Path.Combine(root, "src/shared/Multitenancy.Primitives/Multitenancy.Primitives.csproj");
        var context = Path.Combine(root, "src/shared/Multitenancy/Multitenancy.csproj");
        var projects = Directory
            .EnumerateFiles(Path.Combine(root, "src/features"), "*.*proj", SearchOption.AllDirectories)
            .Where(path => Path.GetFileNameWithoutExtension(path).EndsWith(".Domain", StringComparison.Ordinal))
            .Where(path => Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*", SearchOption.TopDirectoryOnly)
                .Where(source => Path.GetExtension(source) is ".cs" or ".fs")
                .Any(source => File.ReadAllText(source).Contains("ITenantOwned", StringComparison.Ordinal)))
            .ToArray();

        Assert.NotEmpty(projects);

        foreach (var path in projects)
        {
            var references = XDocument.Load(path)
                .Descendants("ProjectReference")
                .Select(reference => Path.GetFullPath(
                    Path.Combine(
                        Path.GetDirectoryName(path)!,
                        reference.Attribute("Include")!.Value.Replace('\\', '/'))))
                .ToArray();

            Assert.Contains(primitives, references);
            Assert.DoesNotContain(context, references);
        }
    }
}
