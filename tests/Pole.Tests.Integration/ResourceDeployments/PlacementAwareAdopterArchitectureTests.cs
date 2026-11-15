using System.Reflection;
using Pole.Features.Postgres.Domain;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

public sealed class PlacementAwareAdopterArchitectureTests
{
    [Theory]
    [InlineData("WebServices")]
    [InlineData("RedisServices")]
    [InlineData("Postgres")]
    [InlineData("SeaweedFS")]
    [InlineData("Functions")]
    public void PlacementAwareAdopter_DoesNotUseTransitionalDefaultTargetOrGroupLookup(string featureName)
    {
        var featurePath = Path.Combine(FindRepositoryRoot(), "src", "features", featureName);
        var source = Directory.EnumerateFiles(featurePath, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) ||
                path.EndsWith(".fs", StringComparison.Ordinal))
            .Where(path => !path.Contains(".Tests", StringComparison.Ordinal))
            .Select(path => new
            {
                Path = path,
                Content = File.ReadAllText(path),
            })
            .ToArray();

        var defaultTargetUsers = source
            .Where(file => file.Content.Contains("GetDefaultAsync(", StringComparison.Ordinal))
            .Select(file => file.Path)
            .ToArray();
        var legacyGroupLookupUsers = source
            .Where(file => file.Content.Contains("IResourceGroupLookup", StringComparison.Ordinal))
            .Select(file => file.Path)
            .ToArray();

        Assert.Empty(defaultTargetUsers);
        Assert.Empty(legacyGroupLookupUsers);
    }

    [Theory]
    [InlineData(typeof(Pole.Features.WebServices.Domain.WebService))]
    [InlineData(typeof(Pole.Features.RedisServices.Domain.Redis))]
    [InlineData(typeof(PostgresCluster))]
    [InlineData(typeof(Pole.Features.SeaweedFS.Domain.SeaweedFSService))]
    [InlineData(typeof(Pole.Features.Functions.Domain.FunctionApp))]
    public void LogicalResourceDomain_DoesNotOwnKubernetesNamespace(Type resourceType)
    {
        var properties = resourceType.Assembly
            .GetTypes()
            .SelectMany(type =>
                type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .ToArray();
        // Placement belongs to ResourceDeployments. Only the provider's independent cleanup record
        // retains the old destination so it can finish deleting native objects after teardown.
        var namespaceOwners = properties.Where(property => property.Name == "KubernetesNamespace")
            .Select(property => property.DeclaringType)
            .ToArray();
        if (resourceType == typeof(PostgresCluster))
            Assert.Equal(typeof(PostgresDatabaseTarget), Assert.Single(namespaceOwners));
        else
            Assert.Empty(namespaceOwners);

        var references = properties.Where(property =>
                RefersTo(property.PropertyType, typeof(PostgresDatabaseTarget)) || RefersTo(
                    property.PropertyType,
                    typeof(PostgresManagedDatabase)))
            .ToArray();
        if (resourceType == typeof(PostgresCluster))
        {
            var cleanupTarget = Assert.Single(references);
            Assert.Equal(typeof(PostgresManagedDatabase), cleanupTarget.DeclaringType);
            Assert.Equal(nameof(PostgresManagedDatabase.Target), cleanupTarget.Name);
        }
        else
            Assert.Empty(references);
    }

    private static bool RefersTo(Type propertyType, Type target) => propertyType == target ||
        propertyType.IsArray && RefersTo(propertyType.GetElementType()!, target) || propertyType.IsGenericType &&
        propertyType.GetGenericArguments().Any(argument => RefersTo(argument, target));

    [Theory]
    [InlineData("WebServices", "WebServices.Application", "WebServices.Application.csproj")]
    [InlineData("RedisServices", "RedisServices.Application", "RedisServices.Application.csproj")]
    [InlineData("Postgres", "Postgres.Application", "Postgres.Application.fsproj")]
    [InlineData("SeaweedFS", "SeaweedFS.Application", "SeaweedFS.Application.csproj")]
    [InlineData("Functions", "Functions.Application", "Functions.Application.csproj")]
    public void PlacementAwareAdopter_ReferencesComputeProfileContractsNotApplication(
        string featureName,
        string applicationProject,
        string projectFile)
    {
        var content = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "src", "features", featureName, applicationProject, projectFile));

        Assert.Contains("ComputeProfiles.Contracts", content, StringComparison.Ordinal);
        Assert.DoesNotContain("ComputeProfiles.Application", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ComputeProfilesApplication_UsesResourceDeploymentContractsOnly()
    {
        var content = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "src",
                "features",
                "ComputeProfiles",
                "ComputeProfiles.Application",
                "ComputeProfiles.Application.csproj"));

        Assert.Contains("ResourceDeployments.Contracts", content, StringComparison.Ordinal);
        Assert.DoesNotContain("ResourceDeployments.Application", content, StringComparison.Ordinal);
        Assert.DoesNotContain("ResourceDeployments.Domain", content, StringComparison.Ordinal);
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

        throw new DirectoryNotFoundException("Could not locate the Pole repository root.");
    }
}
