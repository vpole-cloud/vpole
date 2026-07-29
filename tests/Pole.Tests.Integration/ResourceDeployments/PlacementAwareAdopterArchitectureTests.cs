using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

public sealed class PlacementAwareAdopterArchitectureTests
{
    [Theory]
    [InlineData("WebServices")]
    [InlineData("RedisServices")]
    [InlineData("Postgres")]
    public void PlacementAwareAdopter_DoesNotUseTransitionalDefaultTargetOrGroupLookup(
        string featureName)
    {
        var featurePath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "features",
            featureName);
        var source = Directory
            .EnumerateFiles(featurePath, "*", SearchOption.AllDirectories)
            .Where(path =>
                path.EndsWith(".cs", StringComparison.Ordinal) ||
                path.EndsWith(".fs", StringComparison.Ordinal))
            .Select(path => new
            {
                Path = path,
                Content = File.ReadAllText(path)
            })
            .ToArray();

        var defaultTargetUsers = source
            .Where(file => file.Content.Contains(
                "GetDefaultAsync(",
                StringComparison.Ordinal))
            .Select(file => file.Path)
            .ToArray();
        var legacyGroupLookupUsers = source
            .Where(file => file.Content.Contains(
                "IResourceGroupLookup",
                StringComparison.Ordinal))
            .Select(file => file.Path)
            .ToArray();

        Assert.Empty(defaultTargetUsers);
        Assert.Empty(legacyGroupLookupUsers);
    }

    [Theory]
    [InlineData("WebServices", "WebServices.Domain")]
    [InlineData("RedisServices", "RedisServices.Domain")]
    [InlineData("Postgres", "Postgres.Domain")]
    public void LogicalResourceDomain_DoesNotOwnKubernetesNamespace(
        string featureName,
        string domainProject)
    {
        var domainPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "features",
            featureName,
            domainProject);
        var offenders = Directory
            .EnumerateFiles(domainPath, "*", SearchOption.AllDirectories)
            .Where(path =>
                path.EndsWith(".cs", StringComparison.Ordinal) ||
                path.EndsWith(".fs", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains(
                "KubernetesNamespace",
                StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(offenders);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Pole.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the Pole repository root.");
    }
}
