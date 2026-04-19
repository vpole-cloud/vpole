using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pole.Database;
using Pole.Feature.DomainNames.Common;
using Pole.Features.Postgres.Kubernetes;
using Pole.Features.Postgres.Endpoints;
using Pole.Features.RedisServices.Common;
using Pole.Features.ResourceGroups.Common;
using Pole.Features.ResourceGroups.Services;
using Pole.Features.Search.Common;
using Pole.Features.Search.Services;
using Pole.Features.WebServices.Common;
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
        typeof(DomainNameEndpoints).Assembly,
        typeof(SearchEndpoints).Assembly
    ];

    public static ServiceProvider Create(MongoReplicaSetFixture fixture)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton(fixture.Client);
        services.AddSingleton(fixture.Database);
        services.AddSingleton<MongoDbContext>(_ => fixture.DbContext);
        services.AddSingleton(new KubernetesOptions { NamespaceNamePrefix = "test" });
        services.AddSingleton<IKubernetesNamespaceGenerator, KubernetesNamespaceGenerator>();
        services.AddSingleton<InMemoryPostgresClusterSecretStore>();
        services.AddSingleton<IPostgresClusterSecretStore>(sp => sp.GetRequiredService<InMemoryPostgresClusterSecretStore>());
        services.AddMigrations(Assemblies);
        services.AddSearchFeature();
        services.AddValidatorsFromAssemblies(Assemblies);
        services.AddPoleMediator(Assemblies);

        return services.BuildServiceProvider(validateScopes: true);
    }
}
