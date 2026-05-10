using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pole.Feature.DnsRecords.Core.Common;
using Pole.Features.Postgres.Common;
using Pole.Features.Postgres.Kubernetes;
using Pole.Features.Postgres.Endpoints;
using Pole.Features.RedisServices.Common;
using Pole.Features.ResourceGroups.Common;
using Pole.Features.ResourceGroups.Services;
using Pole.Features.Search.Common;
using Pole.Features.Search.Services;
using Pole.Features.WebServices.Common;
using Pole.Observability;
using Pole.Server.Database;
using Pole.Server.Extensions;

namespace Pole.Tests.Integration.Infrastructure;

public sealed class TestApiApplication : IAsyncDisposable
{
    private readonly WebApplication _app;

    private static readonly Assembly[] Assemblies =
    [
        typeof(WebServiceEndpoints).Assembly,
        typeof(PostgresClusterEndpoints).Assembly,
        typeof(ResourceGroupEndpoints).Assembly,
        typeof(RedisServiceEndpoints).Assembly,
        typeof(DnsRecordEndpoints).Assembly,
        typeof(SearchEndpoints).Assembly
    ];

    private TestApiApplication(WebApplication app)
    {
        _app = app;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    public static async Task<TestApiApplication> StartAsync(MongoReplicaSetFixture fixture)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });

        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(fixture.Client);
        builder.Services.AddSingleton(fixture.Database);
        builder.Services.AddSingleton<MongoDbContext>(_ => fixture.DbContext);
        builder.Services.AddMongoDbContextInterfaces();
        builder.Services.AddSingleton(new KubernetesOptions { NamespaceNamePrefix = "test" });
        builder.Services.AddSingleton(new PostgresSettings
        {
            DefaultDeploymentMode = Pole.Features.Postgres.Domain.PostgresDeploymentMode.Operator,
            SupportedVersions = new PostgresSupportedVersionsSettings { From = 16, To = 17 }
        });
        builder.Services.AddSingleton<IKubernetesNamespaceGenerator, KubernetesNamespaceGenerator>();
        builder.Services.AddSingleton<InMemoryPostgresClusterSecretStore>();
        builder.Services.AddSingleton<IPostgresClusterSecretStore>(sp => sp.GetRequiredService<InMemoryPostgresClusterSecretStore>());
        builder.Services.AddMigrations(Assemblies);
        builder.Services.AddSearchFeature();
        builder.Services.AddValidatorsFromAssemblies(Assemblies);
        builder.Services.AddPoleMediator(Assemblies);
        builder.Services.AddCors(options =>
        {
            options.AddPolicy("frontend-dev", policy =>
                policy.WithOrigins("http://localhost:5173")
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .WithExposedHeaders(TraceHeaders.CorrelationId, TraceHeaders.TraceId));
        });

        var app = builder.Build();
        app.UseTraceContext();
        app.UseCors("frontend-dev");
        app.MapGroup("/api/v1")
            .MapResourceGroupEndpoints()
            .MapWebServiceEndpoints()
            .MapRedisServiceEndpoints()
            .MapPostgresClusterEndpoints()
            .MapSearchEndpoints()
            .MapDomainNameEndpoints();

        await app.StartAsync();

        return new TestApiApplication(app);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
