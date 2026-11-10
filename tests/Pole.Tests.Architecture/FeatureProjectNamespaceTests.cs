using System.Xml.Linq;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class FeatureProjectNamespaceTests
{
    [Fact]
    public void FeatureProjects_DeclareTheirFullRootNamespace()
    {
        var directory = Path.Combine(ArchitectureSources.RepositoryRoot, "src/features");
        var projects = ArchitectureSources.SourceFiles(directory, "*.*proj")
            .Where(path => Path.GetExtension(path) is ".csproj" or ".fsproj")
            .ToArray();

        Assert.NotEmpty(projects);
        Assert.All(
            projects,
            path =>
            {
                var project = XDocument.Load(path);
                var expected = $"Pole.Features.{Path.GetFileNameWithoutExtension(path)}";
                var actual = project.Descendants("RootNamespace").Select(element => element.Value).ToArray();

                Assert.True(
                    actual.Length == 1 && actual[0] == expected,
                    $"{Path.GetRelativePath(directory, path)} must explicitly declare RootNamespace '{expected}'.");
            });
    }
}
