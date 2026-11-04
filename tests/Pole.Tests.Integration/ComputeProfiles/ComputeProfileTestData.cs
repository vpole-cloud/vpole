using MongoDB.Driver;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using Pole.Database;
using Pole.Features.Outbox.Application.Persistence;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ComputeProfiles.Application.Endpoints;
using Pole.Features.ComputeProfiles.Application.Persistence;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Persistence;
using Pole.Features.DeploymentTargets.Application.Persistence.References;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.ResourceDeployments.Application.Persistence;
using Pole.Features.ResourceDeployments.Application.Persistence.Validation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.ResultType;
using Pole.Tests.Integration.DeploymentTargets;
using Pole.Tests.Integration.Infrastructure;

namespace Pole.Tests.Integration.ComputeProfiles;

internal static class ComputeProfileTestData
{
    public const string TenantId = "507f1f77bcf86cd799439098";
    public const string OtherTenantId = "507f1f77bcf86cd799439099";

    public static readonly DateTime CreatedAtUtc =
        new(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);

    public static ComputeProfile CreateProfile(
        string targetId,
        bool isDefault = false,
        bool isEnabled = true,
        ComputeProfileTenantAccess? tenantAccess = null,
        IEnumerable<ResourceTypeId>? resourceTypes = null,
        string? id = null,
        string displayName = "General purpose",
        string? technicalName = null) =>
        ComputeProfileFactory.Create(technicalName ?? "profile-" + (id ?? ObjectId.GenerateNewId().ToString()),
            displayName,
            "Persistence test profile",
            ComputeProfileResourceRequirements.Create(
                ComputeProfileResourceQuantity.Create(250, 256),
                ComputeProfileResourceQuantity.Create(1000, 1024)),
            ComputeArchitecture.Amd64,
            [ComputeProfileExtendedResource.Create("nvidia.com/gpu", 1)],
            resourceTypes ?? [ResourceTypeId.Create("pole/web-service")],
            tenantAccess ?? AllTenantsComputeProfileAccess.Create(),
            [CreateMapping(targetId)],
            isEnabled,
            isDefault,
            new ComputeProfileFactory.CreateOptions(
                id ?? ObjectId.GenerateNewId().ToString(),
                CreatedAtUtc));

    public static ComputeProfileTargetMapping CreateMapping(string targetId)
    {
        var term = ComputeProfileNodeAffinityTerm.Create(
        [
            ComputeProfileNodeAffinityExpression.Create(
                "pole.io/node-pool",
                ComputeNodeAffinityOperator.In,
                ["general"])
        ]);
        return ComputeProfileTargetMapping.Create(
            targetId,
            [ComputeProfileNodeSelector.Create("kubernetes.io/os", "linux")],
            [term],
            [ComputeProfilePreferredNodeAffinityTerm.Create(50, term)],
            [
                ComputeProfileToleration.Create(
                    "workload",
                    ComputeTolerationOperator.Exists,
                    null,
                    ComputeTaintEffect.NoExecute,
                    60)
            ],
            "runc");
    }

    public static DeploymentTarget CreateTarget(string? id = null)
    {
        id ??= ObjectId.GenerateNewId().ToString();
        const long connectionVersion = 1;
        return DeploymentTargetFactory.Create(id == TestData.DefaultDeploymentTargetId ? "lab" : "target-" + id,
                "Compute profile target",
                $"kubernetes:kube-system:{id}",
                DeploymentTargetConnectionFactory.CreateInCluster(connectionVersion),
                DeploymentTargetHealth.Healthy(connectionVersion, CreatedAtUtc),
                DeploymentTargetCapabilities.Unknown(connectionVersion),
                DeploymentTargetTestData.CreateWorkloadConfiguration(),
                enabled: true,
                acceptsWorkloads: true,
                new DeploymentTargetFactory.CreateOptions(id, CreatedAtUtc))
            .AttestForTests();
    }
}

internal sealed class ComputeProfilePersistenceTestHost : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;

    public ComputeProfilePersistenceTestHost(
        MongoReplicaSetFixture fixture,
        ApplyDeploymentTargetReferenceChanges? applyTargetReferenceChanges = null,
        StoreOutboxWrites? storeOutboxWrites = null,
        IMongoDatabase? database = null)
    {
        database ??= fixture.Database;
        var store = new MongoTestStore(database.Client, database);
        var services = new ServiceCollection();
        services.AddSingleton(database.Client);
        services.AddSingleton(database);
        services.AddSingleton(store.ResourceDeployments);
        services.AddSingleton<MongoEnsureComputeProfileUnreferenced>();
        services.AddSingleton<EnsureComputeProfileUnreferenced>(provider =>
            provider
                .GetRequiredService<MongoEnsureComputeProfileUnreferenced>()
                .InvokeAsync);
        services.AddSingleton<MongoRunTransaction>();
        services.AddSingleton<RunMongoOutcomeTransaction<DeleteComputeProfileOutcome>>(provider => provider
            .GetRequiredService<MongoRunTransaction>()
            .InvokeOutcomeAsync<DeleteComputeProfileOutcome>);
        if (applyTargetReferenceChanges is null)
        {
            services.AddSingleton(new MongoApplyDeploymentTargetReferenceChanges(
                store.DeploymentTargets,
                store.DeploymentTargetReferences,
                TimeProvider.System));
            services.AddSingleton<ApplyDeploymentTargetReferenceChanges>(provider => provider
                .GetRequiredService<MongoApplyDeploymentTargetReferenceChanges>()
                .InvokeAsync);
        }
        else
        {
            services.AddSingleton(applyTargetReferenceChanges);
        }

        services.AddOutboxPersistence();
        if (storeOutboxWrites is not null)
            services.AddSingleton(storeOutboxWrites);
        services.AddComputeProfilePersistence();
        _provider = services.BuildServiceProvider(validateScopes: true);
        _scope = _provider.CreateScope();
    }

    public T GetRequiredService<T>() where T : notnull =>
        _scope.ServiceProvider.GetRequiredService<T>();

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
    }
}
