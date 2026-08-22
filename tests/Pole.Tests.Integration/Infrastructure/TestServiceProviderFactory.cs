using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pole.Encryption;
using Pole.Features.ComputeProfiles.Application;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Application.Persistence;
using Pole.Features.DeploymentTargets.Application.Endpoints;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Runtime.Consumers;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Domain;
using Pole.Features.Postgres.Application.Kubernetes;
using Pole.Features.Postgres.Application.Endpoints;
using Pole.Features.Quotas.Application;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application;
using Pole.Features.ResourceDeployments.Application;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Services;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Search.Application.Common;
using Pole.Features.Search.Application.Services;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Application.Common;
using Pole.Mediator;
using Pole.Server.Database;
using Pole.Server.Extensions;

namespace Pole.Tests.Integration.Infrastructure;

public static class TestServiceProviderFactory
{
    private static readonly Assembly[] Assemblies =
    [
        typeof(WebServiceEndpoints).Assembly,
        typeof(ComputeProfileFeature).Assembly,
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
        typeof(StorageProfileEndpoints).Assembly
    ];

    public static ServiceProvider Create(MongoReplicaSetFixture fixture)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(fixture.Client);
        services.AddSingleton(fixture.Database);
        services.AddSingleton<MongoDbContext>(_ => fixture.DbContext);
        services.AddSingleton<IStorageDiscoveryService>(new TestStorageDiscoveryService("standard", "fast", "bulk"));
        services.AddSingleton<IKubernetesClientProvider>(
            new FixedKubernetesClientProvider(null!));
        services.AddMongoDbContextInterfaces();
        services.AddResourceDeploymentsFeature(
            new ConfigurationBuilder().Build());
        services.AddTenantContractCapabilities();
        services.AddDomainPersistence();
        services.AddComputeProfiles();
        services.AddStorageProfileResolution();
        services.AddRedisServicesFeature();
        services.AddWebServicesFeature();
        services.AddScoped<IFunctionRevisionLifecycleService, FunctionRevisionLifecycleService>();
        services.AddSingleton(new KubernetesOptions { NamespaceNamePrefix = "test" });
        services.AddSingleton(new PostgresSettings
        {
            DefaultDeploymentMode = PostgresDeploymentMode.Operator,
            SupportedVersions = new PostgresSupportedVersionsSettings { From = 16, To = 17 }
        });
        services.AddSingleton(new QuotaSettings());
        services.AddQuotaFramework(
            typeof(RedisQuotaProfile).Assembly,
            typeof(FunctionAppQuotaProfile).Assembly,
            typeof(WebServiceQuotaProfile).Assembly,
            typeof(PostgresQuotaProfile).Assembly);
        services.AddSingleton<IKubernetesNamespaceGenerator, KubernetesNamespaceGenerator>();
        var deploymentTargetLookup = new TestDeploymentTargetLookup();
        services.AddSingleton(deploymentTargetLookup);
        services.AddSingleton<IDeploymentTargetLookup>(deploymentTargetLookup);
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
        services.AddSingleton<IDeploymentTargetReferenceAdmission>(
            deploymentTargetLookup);
        services.AddSingleton<IDeploymentTargetWorkloadConfigurationFence>(
            deploymentTargetLookup);
        services.AddScoped<ResourceGroupLookup>();
        services.AddScoped<GetResourceGroupOwnership>(provider =>
            provider.GetRequiredService<ResourceGroupLookup>()
                .GetOwnershipOutcomeAsync);
        services.AddSingleton<IEnvelopeEncryptionService>(
            TestPostgresEnvelopeEncryption.Create());
        services.AddScoped<IPostgresCredentialService, PostgresCredentialService>();
        services.AddMigrations(Assemblies);
        services.AddSearchFeature();
        services.AddScoped<SourceBuildReferenceLookup>();
        services.AddScoped<ISourceBuildReferenceLookup>(provider =>
            provider.GetRequiredService<SourceBuildReferenceLookup>());
        services.AddScoped<EnsureNoWebServiceSourceBuildConfiguration>(provider =>
            provider.GetRequiredService<SourceBuildReferenceLookup>()
                .EnsureNoWebServiceConfigurationAsync);
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
