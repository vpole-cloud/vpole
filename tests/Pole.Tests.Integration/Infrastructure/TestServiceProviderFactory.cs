using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pole.Database;
using Pole.Encryption;
using Pole.Features.ComputeProfiles.Application;
using Pole.Features.Domains.Application;
using Pole.Features.Domains.Application.Common;
using Pole.Features.DeploymentTargets.Application.Endpoints;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application;
using Pole.Features.Functions.Runtime.Consumers;
using Pole.Features.Outbox.Application.Persistence;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Application;
using Pole.Features.Postgres.Domain;
using Pole.Features.Postgres.Application.Kubernetes;
using Pole.Features.Postgres.Application.Endpoints;
using Pole.Features.Quotas.Application;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application;
using Pole.Features.ResourceDeployments.Application;
using Pole.Features.ResourceGroups.Application;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.Search.Application.Common;
using Pole.Features.Search.Application;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Application;
using Pole.Features.Users.Application;
using Pole.Features.Users.Application.Common;
using Pole.Features.SourceBuilds.Application;
using Pole.Features.SourceBuilds.Application.Jobs;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Application.Common;
using Pole.Mediator;
using Pole.Server.Extensions;

namespace Pole.Tests.Integration.Infrastructure;

public static class TestServiceProviderFactory
{
    private static readonly Assembly[] Assemblies =
    [
        typeof(WebServiceEndpoints).Assembly,
        typeof(ComputeProfileApplicationRegistration).Assembly,
        typeof(FunctionAppFeature).Assembly,
        typeof(FunctionRuntimeFeature).Assembly,
        typeof(PostgresClusterEndpoints).Assembly,
        typeof(QuotaEndpoints).Assembly,
        typeof(ResourceGroupEndpoints).Assembly,
        typeof(RedisServiceEndpoints).Assembly,
        typeof(ResourceDeploymentFeature).Assembly,
        typeof(DomainZoneEndpoints).Assembly,
        typeof(DeploymentTargetFeature).Assembly,
        typeof(SearchEndpoints).Assembly,
        typeof(StorageProfileEndpoints).Assembly,
        typeof(TenantEndpoints).Assembly,
        typeof(UserEndpoints).Assembly
    ];

    public static ServiceProvider Create(MongoReplicaSetFixture fixture)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["kubernetes:NamespaceNamePrefix"] = "test"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(fixture.Client);
        services.AddSingleton(fixture.Database);
        services.AddSingleton<MongoRunTransaction>();
        var storageDiscovery = new TestStorageDiscoveryService("standard", "fast", "bulk");
        services.AddSingleton(storageDiscovery);
        services.AddSingleton<DiscoverKubernetesStorageClasses>(
            storageDiscovery.GetStorageClassesAsync);
        services.AddSingleton<DiscoverKubernetesStorageClassNames>(
            storageDiscovery.GetStorageClassNamesAsync);
        services.AddSingleton<DiscoverKubernetesPersistentVolumes>(
            storageDiscovery.GetPersistentVolumesAsync);
        services.AddSingleton<DiscoverKubernetesPersistentVolumeClaims>(
            storageDiscovery.GetPersistentVolumeClaimsAsync);
        services.AddSingleton<IKubernetesClientProvider>(
            new FixedKubernetesClientProvider(null!));
        services.AddOutboxPersistence();
        services.AddFunctionAppsFeature();
        services.AddResourceDeploymentsFeature(
            configuration);
        services.AddResourceGroupsFeature(configuration);
        services.AddTenantsFeature();
        services.AddUsersFeature(configuration);
        services.AddDomainsFeature();
        services.AddComputeProfilesFeature();
        services.AddStorageProfilesFeature();
        services.AddRedisServicesFeature();
        services.AddPostgresFeature(configuration);
        services.AddWebServicesFeature();
        services.AddSingleton(new PostgresSettings
        {
            DefaultDeploymentMode = PostgresDeploymentMode.Operator,
            SupportedVersions = new PostgresSupportedVersionsSettings { From = 16, To = 17 }
        });
        services.AddQuotasFeature(configuration);
        var deploymentTargetLookup = new TestDeploymentTargetLookup();
        services.AddSingleton(deploymentTargetLookup);
        services.AddSingleton<LookupDeploymentTarget>(
            deploymentTargetLookup.LookupAsync);
        services.AddSingleton<LookupDefaultDeploymentTarget>(
            deploymentTargetLookup.LookupDefaultAsync);
        services.AddSingleton<ListEligibleDeploymentTargetsForNewWorkload>(
            deploymentTargetLookup.ListEligibleAsync);
        services.AddSingleton<AdmitDefaultDeploymentTargetForNewWorkload>(
            deploymentTargetLookup.AdmitDefaultForNewWorkloadAsync);
        services.AddSingleton<ListEligiblePublicIngressDeploymentTargetsForNewWorkload>(
            deploymentTargetLookup.ListEligiblePublicIngressForNewWorkloadAsync);
        services.AddSingleton<AdmitDeploymentTargetPublicIngressForNewWorkload>(
            deploymentTargetLookup.AdmitForNewWorkloadAsync);
        services.AddSingleton<AdmitDeploymentTargetForWorkload>(
            deploymentTargetLookup.AdmitForWorkloadAsync);
        services.AddSingleton<AdmitDeploymentTargetForNewWorkload>(
            deploymentTargetLookup.AdmitForWorkloadPlacementAsync);
        services.AddSingleton<AdmitDeploymentTargetPublicIngressForDomainReservation>(
            deploymentTargetLookup.AdmitForDomainReservationAsync);
        services.AddSingleton<ApplyDeploymentTargetReferenceChanges>(
            deploymentTargetLookup.ApplyChangesOutcomeAsync);
        services.AddSingleton<FenceDeploymentTargetWorkloadConfigurations>(
            deploymentTargetLookup.FenceManyOutcomeAsync);
        services.AddSingleton<IEnvelopeEncryptionService>(
            TestPostgresEnvelopeEncryption.Create());
        services.AddMigrations(Assemblies);
        services.AddSearchFeature();
        services.AddSingleton<TestSourceBuildJobDispatcher>();
        services.AddSingleton<DispatchSourceBuildJob>(provider =>
            provider.GetRequiredService<TestSourceBuildJobDispatcher>().Invoke);
        services.AddSourceBuildsFeature(configuration);
        services.AddValidatorsFromAssemblies(Assemblies);
        services.AddPoleMediator(Assemblies);
        services.ReplaceTenantAuthorizationWithFake();

        return services.BuildServiceProvider(validateScopes: true);
    }

    public static void ReplaceTenantAuthorizationWithFake(this IServiceCollection services)
    {
        var descriptor = services.SingleOrDefault(d =>
            d.ServiceType == typeof(IPipelineBehavior<,>) &&
            d.ImplementationType == typeof(TenantAuthorizationBehavior<,>));

        if (descriptor is not null)
        {
            services.Remove(descriptor);
        }

        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(FakeAuthBehaviour<,>));
    }
}
