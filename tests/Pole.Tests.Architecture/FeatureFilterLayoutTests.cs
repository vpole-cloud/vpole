using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class FeatureFilterLayoutTests
{
    [Theory]
    [InlineData("GitOps")]
    public void MigratedFeatures_KeepFilterHelpersInOneFeatureFile(string feature)
    {
        var root = $"src/features/{feature}/{feature}.Application/";
        var helpers = ArchitectureSources.ProductionCSharp()
            .Where(source => source.RelativePath.StartsWith(root, StringComparison.Ordinal))
            .SelectMany(source => source.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(type => type.Identifier.ValueText.EndsWith("Filters", StringComparison.Ordinal))
                .Select(type => source.RelativePath))
            .ToArray();

        Assert.NotEmpty(helpers);
        Assert.All(helpers, path => Assert.Equal($"{root}{feature}Filters.cs", path));
    }
}
