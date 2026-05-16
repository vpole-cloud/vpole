using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pole.Feature.DnsRecords.Core.Common;
using Pole.Features.Postgres.Common;
using Pole.Features.Postgres.Domain;
using Pole.Features.Postgres.Kubernetes;
using Pole.Features.Postgres.Endpoints;
using Pole.Features.RedisServices.Common;
using Pole.Features.ResourceGroups.Common;
using Pole.Features.ResourceGroups.Services;
using Pole.Features.Search.Common;
using Pole.Features.Search.Services;
using Pole.Features.WebServices.Common;
using Pole.Mediator;
using Pole.Server.Database;
using Pole.Server.Extensions;

namespace Pole.Tests.Integration.Infrastructure;

public static class TestServiceProviderFactory
{
    private static readonly Assembly[] Assemblies =
    [
        typeof(WebServiceEndpoints).Assembly,
        typeof(PostgresClusterEndpoints).Assembly,
        typeof(ResourceGroupEndpoints).Assembly,
        typeof(RedisServiceEndpoints).Assembly,
        typeof(DnsRecordEndpoints).Assembly,
        typeof(SearchEndpoints).Assembly
    ];

    public static ServiceProvider Create(MongoReplicaSetFixture fixture)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton(fixture.Client);
        services.AddSingleton(fixture.Database);
        services.AddSingleton<MongoDbContext>(_ => fixture.DbContext);
        services.AddMongoDbContextInterfaces();
        services.AddSingleton(new KubernetesOptions { NamespaceNamePrefix = "test" });
        services.AddSingleton(new PostgresSettings
        {
            DefaultDeploymentMode = PostgresDeploymentMode.Operator,
            SupportedVersions = new PostgresSupportedVersionsSettings { From = 16, To = 17 }
        });
        services.AddSingleton<IKubernetesNamespaceGenerator, KubernetesNamespaceGenerator>();
        services.AddSingleton<InMemoryPostgresClusterSecretStore>();
        services.AddSingleton<IPostgresClusterSecretStore>(sp => sp.GetRequiredService<InMemoryPostgresClusterSecretStore>());
        services.AddMigrations(Assemblies);
        services.AddSearchFeature();
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
