using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Consumers;
using Pole.Features.ResourceDeployments.Application.Endpoints;
using Pole.Features.ResourceDeployments.Application.Jobs;
using Pole.Features.ResourceDeployments.Application.Persistence;
using Pole.Features.ResourceDeployments.Application.Persistence.Creation;
using Pole.Features.ResourceDeployments.Application.Persistence.Finalization;
using Pole.Features.ResourceDeployments.Application.Persistence.Reads;
using Pole.Features.ResourceDeployments.Application.Persistence.Reconciliation;
using Pole.Features.ResourceDeployments.Application.Persistence.Requests;
using Pole.Features.ResourceDeployments.Application.Persistence.Validation;
using Pole.Features.ResourceDeployments.Application.Reconciliation;
using Pole.Features.ResourceDeployments.Contracts;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

public sealed class ResourceDeploymentBoundaryArchitectureTests
{
    private const string ApplicationProjectPath =
        "src/features/ResourceDeployments/ResourceDeployments.Application";

    public static TheoryData<Type> OrchestratorTypes =>
    [
        typeof(RetryResourceDeploymentHandler),
        typeof(ResourceDeploymentRecoveryJob),
        typeof(ResourceDeploymentReconciler),
        typeof(WakeResourceDeploymentsOnRuntimeCellReadyConsumer),
        typeof(ReconcileResourceDeploymentConsumer),
        typeof(DeleteResourceDeploymentConsumer)
    ];

    [Theory]
    [MemberData(nameof(OrchestratorTypes))]
    public void Orchestrators_DoNotDependOnRawMongoOrDbContexts(Type type)
    {
        var dependencies = type
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(dependencies, dependency =>
            dependency.Name.Contains("DbContext", StringComparison.Ordinal) ||
            dependency == typeof(IServiceProvider) ||
            dependency.IsGenericType &&
            dependency.GetGenericTypeDefinition() == typeof(IMongoCollection<>));
    }

    [Fact]
    public void RawMongoCapabilityImplementations_AreInternal()
    {
        Type[] implementationTypes =
        [
            typeof(MongoCreateResourceDeployment),
            typeof(MongoCreateResourceDeploymentSet),
            typeof(MongoRequestResourceDeploymentApply),
            typeof(MongoRequestResourceDeploymentDeletion),
            typeof(MongoRequestExactResourceDeploymentApply),
            typeof(MongoRequestExactResourceDeploymentDeletion),
            typeof(MongoRequestResourceDeploymentSetApply),
            typeof(MongoRequestResourceDeploymentSetDeletion),
            typeof(MongoAdvanceResourceDeploymentRevision),
            typeof(MongoRetryExactResourceDeployment),
            typeof(MongoLoadResourceDeployment),
            typeof(MongoLoadResourceDeployments),
            typeof(MongoLoadResourceDeploymentSet),
            typeof(MongoLoadExactResourceDeployment),
            typeof(MongoLoadResourceDeploymentSets),
            typeof(MongoLoadCurrentResourceDeploymentCompute),
            typeof(MongoEnsureComputeProfileUnreferenced),
            typeof(MongoEnsureNoUnfinishedResourceGroupDeployments),
            typeof(MongoEnsureNoUnfinishedIsolationScopeDeployments),
            typeof(MongoGetResourceDeploymentFinalizationReadiness),
            typeof(MongoPurgeDeletedResourceDeployments),
            typeof(MongoGetExactResourceDeploymentFinalizationReadiness),
            typeof(MongoPurgeExactDeletedResourceDeployment),
            typeof(ResourceDeploymentRetryPersistence),
            typeof(ClaimResourceDeploymentOperation),
            typeof(ResourceDeploymentCompletionPersistence),
            typeof(PersistResourceDeploymentSuccessOperation),
            typeof(PersistResourceDeploymentFailureOperation),
            typeof(LoadResourceDeploymentRecoveryCandidatesOperation),
            typeof(RecoverResourceDeploymentOperation),
            typeof(ResourceDeploymentWakePersistence),
            typeof(ResourceDeploymentFilters)
        ];

        Assert.All(implementationTypes, type => Assert.False(type.IsPublic));
    }

    [Fact]
    public void ResourceDeploymentBoundary_KeepsOnlyTheAdapterStrategyInterface()
    {
        var interfaces = typeof(IResourceDeploymentAdapter).Assembly
            .GetTypes()
            .Where(type =>
                type.IsInterface &&
                type.Namespace?.StartsWith(
                    "Pole.Features.ResourceDeployments",
                    StringComparison.Ordinal) == true)
            .ToArray();

        Assert.Equal([typeof(IResourceDeploymentAdapter)], interfaces);
    }

    [Fact]
    public void Contracts_DoNotRetainLegacyResultCapabilities()
    {
        var legacyContractTypes = typeof(CreateResourceDeployment).Assembly
            .GetTypes()
            .Where(type => type.Name.Contains("Legacy", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(legacyContractTypes);
    }

    [Fact]
    public void LocalCapabilities_KeepOneDelegateAndOperationPerFile()
    {
        var sourceFiles = Directory
            .EnumerateFiles(ApplicationSourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(
                $"{Path.DirectorySeparatorChar}Endpoints{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file).Contains("public delegate", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(sourceFiles);
        Assert.All(sourceFiles, file =>
        {
            var source = File.ReadAllText(file);
            Assert.Equal(1, source.Split("public delegate", StringSplitOptions.None).Length - 1);
            Assert.Equal(
                1,
                source.Split("internal sealed class", StringSplitOptions.None).Length - 1);
        });
    }

    [Fact]
    public void RuntimeMongoFilters_AreOnlyBuiltInTheAggregateFilterCatalog()
    {
        var violations = Directory
            .EnumerateFiles(ApplicationSourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.EndsWith(
                $"{Path.DirectorySeparatorChar}ResourceDeploymentFilters.cs",
                StringComparison.Ordinal))
            .Where(file => !file.Contains(
                $"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(file =>
            {
                var source = File.ReadAllText(file);
                return source.Contains(
                           "Builders<ResourceDeployment>.Filter",
                           StringComparison.Ordinal) ||
                       source.Contains(
                           "FilterDefinition<ResourceDeployment>",
                           StringComparison.Ordinal) ||
                       source.Contains(".Find(deployment =>", StringComparison.Ordinal) ||
                       source.Contains(".Find(session, deployment =>", StringComparison.Ordinal);
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
