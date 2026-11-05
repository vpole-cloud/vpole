using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class FeatureFilterLayoutTests
{
    [Theory]
    [InlineData("GitOps")]
    [InlineData("SourceBuilds")]
    public void MigratedFeatures_KeepWorkflowLayoutAndOneFilterFile(string feature)
    {
        var root = $"src/features/{feature}/{feature}.Application/";
        var sources = ArchitectureSources.ProductionCSharp()
            .Where(source => source.RelativePath.StartsWith(root, StringComparison.Ordinal))
            .ToArray();
        Assert.DoesNotContain(sources, source => source.RelativePath[root.Length..].Split('/')
            .Any(segment => segment is "Persistence" or "Mongo"));

        var helpers = sources.SelectMany(source => source.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(type => type.Identifier.ValueText.EndsWith("Filters", StringComparison.Ordinal))
                .Select(type => source.RelativePath))
            .ToArray();

        Assert.NotEmpty(helpers);
        Assert.All(helpers, path => Assert.Equal($"{root}{feature}Filters.cs", path));
    }
}
