using System.Reflection;
using Hangfire;
using MassTransit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Domain;
using Pole.Platform.Hosting;
using Pole.ResultType;

namespace Pole.Testing;

/// <summary>An in-process host for one statically composed service feature.</summary>
public sealed class FeatureTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly TestEncryptionSettings _encryption;
    public IServiceProvider Services => _app.Services;
    public HttpClient GetClient() => _app.GetTestClient();

    private FeatureTestHost(WebApplication app, TestEncryptionSettings encryption) =>
        (_app, _encryption) = (app, encryption);

    public static async Task<FeatureTestHost> StartAsync(
        IConfiguration configuration,
        Assembly applicationAssembly,
        Assembly eventAssembly,
        Action<IServiceCollection, IConfiguration> addFeature,
        Action<IEndpointRouteBuilder> mapFeatureEndpoints,
        FeatureScopedMigration? migration = null,
        Action<IServiceCollection>? configureServices = null,
        CancellationToken cancellationToken = default)
    {
        var encryption = new TestEncryptionSettings();
        WebApplication? app = null;
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Development",
                ApplicationName = typeof(FeatureTestHost).Assembly.GetName().Name
            });
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["kubernetes:NamespaceNamePrefix"] = "feature-test"
            }).AddConfiguration(encryption.Configuration).AddConfiguration(configuration);
            builder.WebHost.UseTestServer();
            addFeature(builder.Services, builder.Configuration);
            builder.Services.AddPolePlatform(
                builder.Configuration,
                [applicationAssembly],
                [eventAssembly],
                []);
            if (migration is not null)
                builder.Services.AddSingleton<IMigration>(migration);
            builder.Services.AddMassTransit(bus =>
            {
                bus.AddConsumers(PolePlatform.ApplicationAssemblies.Append(applicationAssembly).Distinct().ToArray());
                bus.UsingInMemory((context, cfg) => cfg.ConfigureEndpoints(context));
            });
            builder.Services.AddAuthentication(FeatureTestAuthentication.Scheme)
                .AddScheme<AuthenticationSchemeOptions, FeatureTestAuthentication>(
                    FeatureTestAuthentication.Scheme, _ => { });
            builder.Services.AddAuthorization(options => options.AddPolicy(Policy.Admin,
                policy => policy.RequireClaim(Claims.Roles, Roles.Admin)));
            builder.Services.AddSingleton<RecordedBackgroundJobs>();
            builder.Services.AddSingleton<IBackgroundJobClient>(sp =>
                sp.GetRequiredService<RecordedBackgroundJobs>());
            builder.Services.AddUnavailableSourceBuildTargets();
            configureServices?.Invoke(builder.Services);

            app = builder.Build();
            await app.Services.InitializePolePlatformAsync(cancellationToken);
            app.UsePoleTenantPipeline();
            app.MapPolePlatformEndpoints();
            mapFeatureEndpoints(app.MapGroup("/api/v1").RequireTenantContext().RequireAuthorization());
            await app.StartAsync(cancellationToken);
            return new FeatureTestHost(app, encryption);
        }
        catch
        {
            if (app is not null) await app.DisposeAsync();
            encryption.Dispose();
            throw;
        }
    }

    public async Task SeedTenantAsync(
        string tenantId = TestPlatformDefaults.TenantId,
        string userId = TestPlatformDefaults.UserId,
        string email = TestPlatformDefaults.UserEmail)
    {
        var tenant = TenantFactory.Create("Feature test tenant", tenantId) with
        {
            Members = [TenantFactory.CreateMember(tenantId, userId, email, TenantRole.Owner, isDefault: true)]
        };
        await Services.GetRequiredService<IMongoDatabase>()
            .GetCollection<Tenant>("Tenants").InsertOneAsync(tenant);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        finally
        {
            _encryption.Dispose();
        }
    }
}
