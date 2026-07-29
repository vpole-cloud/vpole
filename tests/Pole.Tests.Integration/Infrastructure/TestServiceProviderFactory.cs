using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pole.Encryption;
using Pole.Features.DnsRecords.Application.Common;
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
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Services;
using Pole.Features.Search.Application.Common;
using Pole.Features.Search.Application.Services;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Contracts;
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
        typeof(FunctionAppFeature).Assembly,
        typeof(FunctionRuntimeFeature).Assembly,
        typeof(PostgresClusterEndpoints).Assembly,
        typeof(QuotaEndpoints).Assembly,
        typeof(ResourceGroupEndpoints).Assembly,
        typeof(RedisServiceEndpoints).Assembly,
        typeof(DnsRecordEndpoints).Assembly,
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
        services.AddMongoDbContextInterfaces();
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
        services.AddSingleton<IDeploymentTargetReferenceAdmission>(
            deploymentTargetLookup);
        services.AddSingleton<IDeploymentTargetWorkloadConfigurationFence>(
            deploymentTargetLookup);
        services.AddSingleton<IEnvelopeEncryptionService>(
            TestPostgresEnvelopeEncryption.Create());
        services.AddScoped<IPostgresCredentialService, PostgresCredentialService>();
        services.AddMigrations(Assemblies);
        services.AddSearchFeature();
        services.AddScoped<ISourceBuildReferenceLookup, SourceBuildReferenceLookup>();
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
