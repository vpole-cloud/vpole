using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class FeatureFilterLayoutTests
{
    [Fact]
    public void ApplicationWorkflows_HaveNoPersistenceOrNestedMongoFolders()
    {
        var paths = ArchitectureSources.ProductionCSharp()
            .Select(source => source.RelativePath)
            .Concat(ArchitectureSources.ProductionFSharp().Select(source => source.RelativePath))
            .Where(IsFeatureApplication)
            .ToArray();

        Assert.NotEmpty(paths);
        Assert.DoesNotContain(paths, path => path.Split('/')[4..].Any(segment => segment == "Persistence"));
        Assert.DoesNotContain(paths, path => path.Split('/')[5..].Any(segment => segment == "Mongo"));
    }

    [Fact]
    public void CSharpRuntimeFilterHelpers_UseOneFeatureMongoCatalog()
    {
        var helpers = ArchitectureSources.ProductionCSharp()
            .Where(source => IsFeatureApplication(source.RelativePath))
            .SelectMany(source =>
                source.Root
                    .DescendantNodes()
                    .OfType<ClassDeclarationSyntax>()
                    .Where(type => type.Identifier.ValueText.EndsWith("Filters", StringComparison.Ordinal))
                    .Select(type => source.RelativePath))
            .ToArray();

        Assert.NotEmpty(helpers);
        Assert.All(helpers, path => Assert.Equal(ExpectedCatalog(path, "cs"), path));
    }

    [Fact]
    public void FSharpRuntimeFilterHelpers_UseOneFeatureMongoCatalog()
    {
        var helpers = ArchitectureSources.ProductionFSharp()
            .Where(source => IsFeatureApplication(source.RelativePath))
            .Where(source => Regex.IsMatch(
                source.Text,
                @"^(?:module|type)\s+(?:(?:internal|private)\s+)?\w*Filters\b",
                RegexOptions.Multiline))
            .Select(source => source.RelativePath)
            .ToArray();

        Assert.NotEmpty(helpers);
        Assert.All(helpers, path => Assert.Equal(ExpectedCatalog(path, "fs"), path));
    }

    private static bool IsFeatureApplication(string path)
    {
        var parts = path.Split('/');
        return parts.Length >= 5 && parts[0] == "src" && parts[1] == "features" &&
            parts[3] == $"{parts[2]}.Application";
    }

    private static string ExpectedCatalog(string path, string extension)
    {
        var feature = path.Split('/')[2];
        return $"src/features/{feature}/{feature}.Application/Mongo/{feature}Filters.{extension}";
    }
}
