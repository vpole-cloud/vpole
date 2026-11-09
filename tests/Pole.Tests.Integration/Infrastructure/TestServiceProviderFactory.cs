using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pole.Database;
using Pole.Encryption;
using Pole.Features.ComputeProfiles.Application;
using Pole.Features.DeploymentTargets.Application;
using Pole.Features.DeploymentTargets.Application.Admission;
using Pole.Features.DeploymentTargets.Application.Endpoints;
using Pole.Features.DeploymentTargets.Application.Mongo;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Application;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Functions.Application;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Runtime.Consumers;
using Pole.Features.GitOps.Application;
using Pole.Features.GitProviders.Application;
using Pole.Features.Outbox.Application;
using Pole.Features.Outbox.Application.Mongo;
using Pole.Features.Postgres.Application;
using Pole.Features.Quotas.Application;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.RedisServices.Application;
using Pole.Features.ResourceConnections.Application;
using Pole.Features.ResourceDeployments.Application;
using Pole.Features.ResourceGroups.Application;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Search.Application;
using Pole.Features.Search.Application.Common;
using Pole.Features.Search.Contracts;
using Pole.Features.Secrets.Application;
using Pole.Features.SourceBuilds.Application;
using Pole.Features.SourceBuilds.Application.Jobs;
using Pole.Features.StorageProfiles.Application;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.Tenants.Application;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Users.Application;
using Pole.Features.Users.Application.Common;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Application.Common;
using Pole.Mediator;
using Pole.Platform.Hosting;
using Pole.Server.Extensions;
using Pole.Workloads;
using Pole.Testing.Kubernetes;

namespace Pole.Tests.Integration.Infrastructure;

public static class TestServiceProviderFactory
{
    private static readonly Assembly[] Assemblies =
    [
        typeof(GitOpsFeature).Assembly,
        typeof(SecretFeature).Assembly,
        typeof(WebServiceEndpoints).Assembly,
        typeof(ComputeProfileApplicationRegistration).Assembly,
        typeof(FunctionAppFeature).Assembly,
        typeof(FunctionRuntimeFeature).Assembly,
        typeof(QuotaEndpoints).Assembly,
        typeof(ResourceGroupEndpoints).Assembly,
        typeof(ResourceDeploymentFeature).Assembly,
        typeof(ResourceConnectionsFeature).Assembly,
        typeof(DomainZoneEndpoints).Assembly,
        typeof(DeploymentTargetFeature).Assembly,
        typeof(SearchEndpoints).Assembly,
        typeof(StorageProfileEndpoints).Assembly,
        typeof(TenantEndpoints).Assembly,
        typeof(UserEndpoints).Assembly
    ];

    public static ServiceProvider Create(
        TestDatabaseScope fixture,
        Action<IServiceCollection>? configure = null,
        Pole.Testing.TestTimings? timings = null,
        IntegrationTestScope? testScope = null)
    {
        using var registration = timings?.Measure("mediator.services.register");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["kubernetes:NamespaceNamePrefix"] = "test"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(fixture.Client);
        services.AddSingleton(fixture.Database);
        services.AddSingleton<MongoRunTransaction>();
        var storageDiscovery = new TestStorageDiscoveryService("standard", "fast", "bulk")
        {
            TenantId = testScope?.TenantId ?? string.Empty
        };
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
        services.AddGitOpsFeature();
        services.AddOutboxPersistence();
        services.AddFunctionAppsFeature();
        services.AddResourceDeploymentsFeature(
            configuration);
        services.AddResourceGroupsFeature(configuration);
        services.AddRedisServicesFeature();
        services.AddPostgresFeature(configuration);
        services.AddResourceConnectionsFeature();
        services.AddScoped<EnsureRuntimeCellIngressBaseline>(_ =>
            (request, cancellationToken) =>
                Task.FromResult<EnsureRuntimeCellIngressBaselineOutcome>(
                    new RuntimeCellIngressBaselineReady()));
        services.AddSecretsFeature();
        services.AddTenantsFeature();
        services.AddUsersFeature(configuration);
        services.AddDomainsFeature();
        services.AddComputeProfilesFeature();
        services.AddDeploymentTargetPersistence();
        services.AddStorageProfilesFeature();
        services.AddWebServicesFeature();
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
            TestEnvelopeEncryption.Create());
        services.AddFeatureMigrations("pole.postgres", typeof(
            Pole.Features.Postgres.Migrations.PostgresNameIndexesMigration).Assembly);
        services.AddFeatureMigrations("pole.redis", typeof(
            Pole.Features.RedisServices.Migrations.RedisNameIndexesMigration).Assembly);
        services.AddMigrations([
            .. PolePlatform.MigrationAssemblies,
            typeof(Pole.Features.WebServices.Migrations.WebServiceSchemaValidationMigration).Assembly,
            typeof(Pole.Features.Functions.Migrations.FunctionAppsInitialMigration).Assembly,
            typeof(Pole.Features.ResourceConnections.Migrations.ResourceConnectionIndexesMigration).Assembly
        ]);
        services.AddSingleton(new SearchSourceType(
            ResourceTypeId.Create("third-party/widget"),
            "Widget",
            3));
        services.AddSearchFeature();
        services.AddSingleton<TestSourceBuildJobDispatcher>();
        services.AddSingleton<DispatchSourceBuildJob>(provider =>
            provider.GetRequiredService<TestSourceBuildJobDispatcher>().Invoke);
        services.AddGitProvidersFeature(configuration);
        services.AddSourceBuildsFeature(configuration);
        services.AddValidatorsFromAssemblies(Assemblies);
        services.AddPoleMediator(Assemblies);
        services.ReplaceTenantAuthorizationWithFake();

        configure?.Invoke(services);
        registration?.Dispose();
        using var build = timings?.Measure("mediator.services.build");
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
