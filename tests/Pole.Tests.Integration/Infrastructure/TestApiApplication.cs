using System.Reflection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pole.ASP.Helpers;
using Pole.Features.DnsRecords.Application.Common;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Application.Kubernetes;
using Pole.Features.Postgres.Application.Endpoints;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Services;
using Pole.Features.Search.Application.Common;
using Pole.Features.Search.Application.Services;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Middleware;
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
        typeof(SearchEndpoints).Assembly,
        typeof(StorageProfileEndpoints).Assembly
    ];

    private TestApiApplication(WebApplication app)
    {
        _app = app;
        Client = app.GetTestClient();
        Client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, TestData.TenantId);
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
        builder.Services.AddSingleton<IStorageDiscoveryService>(new TestStorageDiscoveryService("standard", "fast", "bulk"));
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
        builder.Services
            .AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                TestAuthenticationHandler.SchemeName,
                _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(Policy.Admin, policy =>
                policy.RequireClaim(Claims.Roles, Roles.Admin));
        });
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
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<TenantContextMiddleware>();
        app.MapGroup("/api/v1")
            .RequireAuthorization()
            .MapResourceGroupEndpoints()
            .MapWebServiceEndpoints()
            .MapRedisServiceEndpoints()
            .MapPostgresClusterEndpoints()
            .MapSearchEndpoints()
            .MapDomainNameEndpoints()
            .MapStorageProfileEndpoints();

        await app.StartAsync();

        return new TestApiApplication(app);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

