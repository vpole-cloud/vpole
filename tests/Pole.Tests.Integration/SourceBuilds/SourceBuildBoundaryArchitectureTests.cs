using MongoDB.Driver;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Mediator;
using Xunit;

namespace Pole.Tests.Integration.SourceBuilds;

public sealed class SourceBuildBoundaryArchitectureTests
{
    private const string ApplicationProjectPath =
        "src/features/SourceBuilds/SourceBuilds.Application";

    [Fact]
    public void MediatorHandlers_DoNotDependOnRawMongoOrFeatureDbContexts()
    {
        var handlers = typeof(SourceBuildEndpoints).Assembly
            .GetTypes()
            .Where(type => type.GetInterfaces().Any(@interface =>
                @interface.IsGenericType &&
                @interface.GetGenericTypeDefinition() == typeof(IMediatorOutcomeHandler<,>)))
            .ToArray();

        Assert.NotEmpty(handlers);
        Assert.All(handlers, handler =>
        {
            var dependencies = handler
                .GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => parameter.ParameterType)
                .ToArray();

            Assert.DoesNotContain(dependencies, dependency =>
                dependency.Name.Contains("DbContext", StringComparison.Ordinal) ||
                dependency == typeof(IServiceProvider) ||
                dependency.IsGenericType &&
                dependency.GetGenericTypeDefinition() == typeof(IMongoCollection<>));
        });
    }

    [Fact]
    public void ApplicationHasNoFeatureOwnedServiceInterfaces()
    {
        var interfaces = typeof(SourceBuildEndpoints).Assembly
            .GetTypes()
            .Where(type =>
                type.IsInterface &&
                type.Namespace?.StartsWith(
                    "Pole.Features.SourceBuilds.Application",
                    StringComparison.Ordinal) == true)
            .ToArray();

        Assert.Empty(interfaces);
    }

    [Fact]
    public void LocalCapabilities_KeepOneDelegateAndItsImplementationPerFile()
    {
        var sourceFiles = Directory
            .EnumerateFiles(ApplicationSourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("public delegate", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(sourceFiles);
        Assert.All(sourceFiles, file =>
        {
            var source = File.ReadAllText(file);
            Assert.Equal(1, source.Split("public delegate", StringSplitOptions.None).Length - 1);
            Assert.Contains("sealed class", source, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void RuntimeMongoFilters_AreOnlyBuiltInTheAggregateFilterCatalog()
    {
        var violations = Directory
            .EnumerateFiles(ApplicationSourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.EndsWith(
                $"{Path.DirectorySeparatorChar}SourceBuildsFilters.cs",
                StringComparison.Ordinal))
            .Where(file => !file.Contains(
                $"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(file =>
            {
                var source = File.ReadAllText(file);
                return (source.Contains("Builders<", StringComparison.Ordinal) &&
                        source.Contains(".Filter", StringComparison.Ordinal)) ||
                       source.Contains("FilterDefinition<", StringComparison.Ordinal);
            })
            .ToArray();

        Assert.Empty(violations);
    }

    private static string ApplicationSourceDirectory => Path.Combine(
        RepositoryRoot,
        ApplicationProjectPath);

    private static string RepositoryRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
                 directory is not null;
                 directory = directory.Parent)
            {
                var gitDirectory = Path.Combine(directory.FullName, ".git");
                if (Directory.Exists(gitDirectory) || File.Exists(gitDirectory))
                    return directory.FullName;
            }

            throw new DirectoryNotFoundException("Could not locate the repository root.");
        }
    }
}
