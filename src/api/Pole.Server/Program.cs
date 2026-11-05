using Pole.Features.GitOps.Application;
using Pole.Features.Secrets.Application;
using Pole.Features.Realtime.Application;
using System.Reflection;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Encryption;
using Pole.Features.ContainerRegistries.Application;
using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.ComputeProfiles.Application;
using Pole.Features.ComputeProfiles.Application.Bootstrap;
using Pole.Features.DeploymentTargets.Application;
using Pole.Features.DeploymentTargets.Application.Bootstrap;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Endpoints;
using Pole.Features.Domains.Application;
using Pole.Features.Domains.Application.Common;
using Pole.Features.GitProviders.Application;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Application;
using Pole.Features.Functions.Runtime;
using Pole.Features.Functions.Runtime.Consumers;
using Pole.Features.Functions.Runtime.Gateway;
using Pole.Features.Outbox.Application;
using Pole.Features.Quotas.Application;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.Postgres.Application;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.RedisServices.Application;
using Pole.Features.ResourceConnections.Application;
using Pole.Features.ResourceDeployments.Application;
using Pole.Features.ResourceGroups.Application;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.Search.Application.Common;
using Pole.Features.Search.Application;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Application;
using Pole.Features.Tenants.Application.Middleware;
using Pole.Features.Users.Application;
using Pole.Features.Users.Application.Common;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Application.Common;
using Pole.Observability;
using Pole.Server.Extensions;
using Pole.Platform.Hosting;
using Pole.ServiceDefaults;
using Pole.Features.PrivateAccess.Runtime;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
Assembly[] serviceAssemblies =
[
    typeof(WebServiceEndpoints).Assembly, typeof(FunctionAppFeature).Assembly,
    typeof(FunctionRuntimeFeature).Assembly,
    typeof(RedisServicesFeatureRegistration).Assembly,
    typeof(ResourceConnectionsFeature).Assembly,
    typeof(PostgresFeatureRegistration).Assembly
];
Assembly[] serviceEventAssemblies =
[
    typeof(Pole.Features.WebServices.Events.WebServiceCreatedEvent).Assembly,
    typeof(Pole.Features.Functions.Events.FunctionAppCreatedEvent).Assembly,
    typeof(Pole.Features.RedisServices.Events.RedisServiceCreatedEvent).Assembly,
    typeof(Pole.Features.ResourceConnections.Events.ResourceConnectionCreatedEvent).Assembly,
    typeof(Pole.Features.Postgres.Events.PostgresClusterCreatedEvent).Assembly
];
Assembly[] assemblies = [.. PolePlatform.ApplicationAssemblies, .. serviceAssemblies];

builder.ConfigurePrivateAccessListener();
builder.AddServiceDefaults();
builder.AddMongoDBClient("pole-database");
builder.AddFunctionOrleansHosting();
builder.AddSecurity();

builder.Services.AddPolePlatform(configuration,
        serviceAssemblies,
        serviceEventAssemblies,
        [],
        development: builder.Environment.IsDevelopment())
    .AddEventBus(configuration, assemblies)
    .AddFunctionAppsFeature()
    .AddFunctionRuntimeFeature(configuration)
    .AddPrivateAccessRuntime(configuration)
    .AddWebServicesFeature()
    .AddRedisServicesFeature()
    .AddResourceConnectionsFeature()
    .AddPostgresFeature(configuration)
    .AddServiceMigrations()
    .AddHangfireWithMongo(configuration)
    .AddOpenApi();
builder.Services.AddCors(options =>
{
    var frontendOrigin = configuration["Cors:FrontendOrigin"] ?? "http://localhost:5173";
    options.AddPolicy("frontend-dev",
        policy =>
            policy.WithOrigins(frontendOrigin)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .WithExposedHeaders(TraceHeaders.CorrelationId, TraceHeaders.TraceId));
});

var app = builder.Build();

await app.Services.InitializePolePlatformAsync();
await app.Services.GetRequiredService<BootstrapDeploymentTargets>()();
await app.Services.GetRequiredService<BootstrapComputeProfiles>()();

var functionGatewayOptions = app.Services.GetRequiredService<FunctionGatewayOptions>();
app.MapWhen(
    context => context.Connection.LocalPort == functionGatewayOptions.PublicPort ||
               context.Connection.LocalPort == functionGatewayOptions.InternalPort,
    functionApp =>
    {
        functionApp.UseTraceContext();
        functionApp.UseFunctionListenerIsolation();
        functionApp.Run(FunctionGatewayEndpointMapping.DispatchAsync);
    });

app.UsePrivateAccessRuntime();
if (configuration.GetValue<bool>($"{PrivateAccessBrokerOptions.Section}:Enabled"))
{
    app.MapPrivateAccessGatewayControlEndpoint();
    app.MapPrivateAccessGatewayStreamEndpoint();
    app.MapPrivateAccessStreamEndpoint();
}

app.MapDefaultEndpoints();
app.UseTraceContext();

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseRealtimeOriginGuard();
app.UseCors("frontend-dev");
app.UsePoleTenantPipeline();

app.UseHangfire();
app.Services
    .ScheduleGitOpsRecurringJobs()
    .ScheduleOutboxRecurringJobs()
    .ScheduleSourceBuildRecurringJobs()
    .ScheduleResourceGroupRecurringJobs()
    .ScheduleResourceDeploymentRecurringJobs()
    .ScheduleResourceConnectionRecurringJobs()
    .ScheduleSecretProjectionRecurringJobs()
    .ScheduleContainerRegistryRecurringJobs();

app.MapGitHubCallbackEndpoints();
app.MapPoleCliDiscovery(configuration);

app.MapGet("/test-keycloak", () => "Hello World!")
    .RequireAuthorization();

app.MapApiEndpoints();
app.Run();
