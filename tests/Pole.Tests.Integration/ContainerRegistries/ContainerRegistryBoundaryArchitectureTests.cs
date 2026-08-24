using MongoDB.Driver;
using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.ContainerRegistries.Application.Endpoints;
using Pole.Mediator;
using Xunit;

namespace Pole.Tests.Integration.ContainerRegistries;

public sealed class ContainerRegistryBoundaryArchitectureTests
{
    private const string ApplicationProjectPath =
        "src/features/ContainerRegistries/ContainerRegistries.Application";

    [Fact]
    public void MediatorHandlers_DoNotDependOnRawMongoOrFeatureDbContexts()
    {
        var handlers = typeof(ContainerRegistryEndpoints).Assembly
            .GetTypes()
            .Where(type => type.GetInterfaces().Any(@interface =>
                @interface.IsGenericType &&
                @interface.GetGenericTypeDefinition() == typeof(IMediatorOutcomeHandler<,>)))
            .ToArray();

        Assert.NotEmpty(handlers);
        Assert.All(handlers, handler =>
        {
            var dependencies = handler.GetConstructors()
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
    public void LocalCapabilities_KeepOneDelegateAndImplementationPerFile()
    {
        var sourceFiles = Directory.EnumerateFiles(
                ApplicationSourceDirectory,
                "*.cs",
                SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("public delegate", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(sourceFiles);
        Assert.All(sourceFiles, file =>
        {
            var source = File.ReadAllText(file);
            Assert.Equal(1, source.Split("public delegate", StringSplitOptions.None).Length - 1);
            Assert.Equal(1, source.Split("sealed class", StringSplitOptions.None).Length - 1);
        });
    }

    [Fact]
    public void RuntimeMongoFilters_AreOnlyBuiltInTheAggregateFilterCatalog()
    {
        var violations = Directory.EnumerateFiles(
                ApplicationSourceDirectory,
                "*.cs",
                SearchOption.AllDirectories)
            .Where(file => !file.EndsWith(
                $"{Path.DirectorySeparatorChar}ContainerRegistryFilters.cs",
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
