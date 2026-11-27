// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.GitProviders.Application.Common;
using Pole.Mediator;
using Xunit;

namespace Pole.Tests.Integration.GitProviders;

public sealed class GitProviderBoundaryArchitectureTests
{
    private const string ApplicationProjectPath = "src/features/GitProviders/GitProviders.Application";

    private static string ApplicationSourceDirectory => Path.Combine(RepositoryRoot, ApplicationProjectPath);

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

    [Fact]
    public void MediatorHandlers_DoNotDependOnRawMongoOrFeatureDbContexts()
    {
        var handlers = typeof(GitProviderEndpoints).Assembly
            .GetTypes()
            .Where(type =>
                type.GetInterfaces()
                    .Any(@interface => @interface.IsGenericType &&
                        @interface.GetGenericTypeDefinition() == typeof(IMediatorOutcomeHandler<,>)))
            .ToArray();

        Assert.NotEmpty(handlers);
        Assert.All(
            handlers,
            handler =>
            {
                var dependencies = handler.GetConstructors()
                    .SelectMany(constructor => constructor.GetParameters())
                    .Select(parameter => parameter.ParameterType)
                    .ToArray();

                Assert.DoesNotContain(
                    dependencies,
                    dependency => dependency.Name.Contains("DbContext", StringComparison.Ordinal) ||
                        dependency == typeof(IServiceProvider) || (dependency.IsGenericType &&
                            dependency.GetGenericTypeDefinition() == typeof(IMongoCollection<>)));
            });
    }

    [Fact]
    public void GitHubClient_IsTheOnlyRetainedApplicationInterface()
    {
        var interfaces = typeof(GitProviderEndpoints).Assembly
            .GetTypes()
            .Where(type => type.IsInterface && type.Namespace?.StartsWith(
                "Pole.Features.GitProviders.Application",
                StringComparison.Ordinal) == true)
            .ToArray();

        Assert.Equal([typeof(IGitHubAppClient)], interfaces);
    }

    [Fact]
    public void LocalCapabilities_KeepOneDelegateAndImplementationPerFile()
    {
        var sourceFiles = Directory.EnumerateFiles(ApplicationSourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("public delegate", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(sourceFiles);
        Assert.All(
            sourceFiles,
            file =>
            {
                var source = File.ReadAllText(file);
                Assert.Equal(1, source.Split("public delegate").Length - 1);
                Assert.Equal(1, source.Split("sealed class").Length - 1);
            });
    }

    [Fact]
    public void RuntimeMongoFilters_AreOnlyBuiltInTheAggregateFilterCatalog()
    {
        var violations = Directory.EnumerateFiles(ApplicationSourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.EndsWith(
                $"{Path.DirectorySeparatorChar}GitProvidersFilters.cs",
                StringComparison.Ordinal))
            .Where(file => !file.Contains(
                $"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(file =>
            {
                var source = File.ReadAllText(file);
                return (source.Contains("Builders<", StringComparison.Ordinal) &&
                    source.Contains(".Filter", StringComparison.Ordinal)) || source.Contains(
                    "FilterDefinition<",
                    StringComparison.Ordinal);
            })
            .ToArray();

        Assert.Empty(violations);
    }
}
