using System.Reflection;
using Pole.Features.Backups.Application;
using Pole.Features.Backups.Events;
using Pole.Features.ComputeProfiles.Application.Bootstrap;
using Pole.Features.ContainerRegistries.Application;
using Pole.Features.DeploymentTargets.Application.Bootstrap;
using Pole.Features.Functions.Application;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Events;
using Pole.Features.Functions.Runtime;
using Pole.Features.Functions.Runtime.Consumers;
using Pole.Features.Functions.Runtime.Gateway;
using Pole.Features.GitOps.Application;
using Pole.Features.Outbox.Application;
using Pole.Features.Postgres.Application;
using Pole.Features.Postgres.Events;
using Pole.Features.PrivateAccess.Runtime;
using Pole.Features.Realtime.Application;
using Pole.Features.RedisServices.Application;
using Pole.Features.RedisServices.Events;
using Pole.Features.ResourceConnections.Application;
using Pole.Features.ResourceConnections.Events;
using Pole.Features.ResourceDeployments.Application;
using Pole.Features.ResourceGroups.Application;
using Pole.Features.Secrets.Application;
using Pole.Features.SourceBuilds.Application;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Events;
using Pole.Observability;
using Pole.Platform.Hosting;
using Pole.Server.Extensions;
using Pole.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
Assembly[] serviceAssemblies =
[
    typeof(WebServiceEndpoints).Assembly, typeof(FunctionAppFeature).Assembly,
    typeof(FunctionRuntimeFeature).Assembly,
    typeof(BackupsFeature).Assembly,
    typeof(RedisServicesFeatureRegistration).Assembly,
    typeof(ResourceConnectionsFeature).Assembly,
    typeof(PostgresFeatureRegistration).Assembly,
];
Assembly[] serviceEventAssemblies =
[
    typeof(WebServiceCreatedEvent).Assembly,
    typeof(FunctionAppCreatedEvent).Assembly,
    typeof(BackupConfigurationChangedEvent).Assembly,
    typeof(RedisServiceCreatedEvent).Assembly,
    typeof(ResourceConnectionCreatedEvent).Assembly,
    typeof(PostgresClusterCreatedEvent).Assembly,
];
Assembly[] assemblies = [.. PolePlatform.ApplicationAssemblies, .. serviceAssemblies];

builder.ConfigurePrivateAccessListener();
builder.AddServiceDefaults();
builder.AddMongoDBClient("pole-database");
builder.AddFunctionOrleansHosting();
builder.AddSecurity();

builder.Services
    .AddPolePlatform(
        configuration,
        serviceAssemblies,
        serviceEventAssemblies,
        [],
        development: builder.Environment.IsDevelopment())
    .AddEventBus(configuration, assemblies)
    .AddFunctionAppsFeature()
    .AddFunctionRuntimeFeature(configuration)
    .AddPrivateAccessRuntime(configuration)
    .AddWebServicesFeature()
    .AddBackupsFeature()
    .AddRedisServicesFeature()
    .AddResourceConnectionsFeature()
    .AddPostgresFeature(configuration)
    .AddServiceMigrations()
    .AddHangfireWithMongo(configuration)
    .AddOpenApi();
builder.Services.AddCors(options =>
{
    var frontendOrigin = configuration["Cors:FrontendOrigin"] ?? "http://localhost:5173";
    options.AddPolicy(
        "frontend-dev",
        policy => policy.WithOrigins(frontendOrigin)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .WithExposedHeaders(TraceHeaders.CorrelationId, TraceHeaders.TraceId));
});

var app = builder.Build();

await app.Services.InitializePolePlatformAsync();
app.Services.ValidateBackupProviders();
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
    app.UseHttpsRedirection();

app.UseRealtimeOriginGuard();
app.UseCors("frontend-dev");
app.UsePoleTenantPipeline();

app.MapDevelopmentHangfireDashboard();
app.Services
    .ScheduleBackupRecurringJobs()
    .ScheduleGitOpsRecurringJobs()
    .ScheduleOutboxRecurringJobs()
    .ScheduleSourceBuildRecurringJobs()
    .ScheduleResourceGroupRecurringJobs()
    .ScheduleResourceDeploymentRecurringJobs()
    .ScheduleResourceConnectionRecurringJobs()
    .ScheduleSecretProjectionRecurringJobs()
    .ScheduleContainerRegistryRecurringJobs();

app.MapPoleCliDiscovery(configuration);

app.MapGet("/test-keycloak", () => "Hello World!").RequireAuthorization();

app.MapApiEndpoints();
app.Run();
