using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Consumers;
using Pole.Features.ResourceDeployments.Application.Endpoints;
using Pole.Features.ResourceDeployments.Application.Jobs;
using Pole.Features.ResourceDeployments.Application.Mutations;
using Pole.Features.ResourceDeployments.Application.Persistence;
using Pole.Features.ResourceDeployments.Application.Placement;
using Pole.Features.ResourceDeployments.Application.Reconciliation;
using Pole.Features.ResourceDeployments.Contracts;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

public sealed class ResourceDeploymentBoundaryArchitectureTests
{
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
            typeof(ResourceDeploymentWriter),
            typeof(ResourceDeploymentMutationWriter),
            typeof(ResourceDeploymentQueryService),
            typeof(ResourceDeploymentReferenceLookup),
            typeof(ResourceDeploymentComputeLookup),
            typeof(ResourceDeploymentComputeProfileReferenceGuard),
            typeof(ResourceDeploymentFinalizationService),
            typeof(ResourceDeploymentRetryPersistence),
            typeof(ResourceDeploymentReconciliationPersistence),
            typeof(ResourceDeploymentRecoveryPersistence),
            typeof(ResourceDeploymentWakePersistence),
            typeof(ResourceDeploymentFilters),
            typeof(ResourceDeploymentPlacementResolver)
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
}
