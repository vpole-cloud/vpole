using System.Reflection;
using Pole.Database;
using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.DnsRecords.Application.Common;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Runtime.Consumers;
using Pole.Features.Functions.Runtime.Gateway;
using Pole.Features.Outbox.Application;
using Pole.Features.Postgres.Application.Endpoints;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Quotas.Application;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.Search.Application.Common;
using Pole.Features.Search.Application.Services;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Application.Middleware;
using Pole.Features.Users.Application.Common;
using Pole.Features.WebServices.Application.Common;
using Pole.Observability;
using Pole.Server.Extensions;
using Pole.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
Assembly[] assemblies = [
    typeof(WebServiceEndpoints).Assembly,
    typeof(FunctionAppFeature).Assembly,
    typeof(FunctionRuntimeFeature).Assembly,
    typeof(PostgresClusterEndpoints).Assembly,
    typeof(QuotaEndpoints).Assembly,
    typeof(DnsRecordEndpoints).Assembly,
    typeof(GitProviderEndpoints).Assembly,
    typeof(ContainerRegistryFeature).Assembly,
    typeof(SourceBuildFeature).Assembly,
    typeof(OutboxProcessorJob).Assembly,
    typeof(ResourceGroupEndpoints).Assembly,
    typeof(RedisServiceEndpoints).Assembly,
    typeof(SearchEndpoints).Assembly,
    typeof(StorageProfileEndpoints).Assembly,
    typeof(TenantEndpoints).Assembly,
    typeof(UserEndpoints).Assembly,
];
Assembly[] outboxEventAssemblies = [
    typeof(Pole.Features.WebServices.Events.WebServiceCreatedEvent).Assembly,
    typeof(Pole.Features.Functions.Events.FunctionAppCreatedEvent).Assembly,
    typeof(Pole.Features.DnsRecords.Events.CommitDomainNameEvent).Assembly,
    typeof(Pole.Features.Postgres.Events.PostgresClusterCreatedEvent).Assembly,
    typeof(Pole.Features.RedisServices.Events.RedisServiceCreatedEvent).Assembly,
    typeof(Pole.Features.ResourceGroups.Events.ResourceGroupCreatedEvent).Assembly,
    typeof(Pole.Features.SourceBuilds.Events.SourceBuildRequestedEvent).Assembly,
    typeof(Pole.Features.Users.Contracts.UserCreatedEvent).Assembly
];

builder.AddServiceDefaults();
builder.AddMongoDBClient("pole-database");
builder.AddFunctionOrleansHosting();
builder.AddSecurity();

builder.Services.AddKubernetes(configuration)
    .AddEventBus(configuration, assemblies)
    .AddOutboxEventAssemblies(outboxEventAssemblies)
    .AddDatabase()
    .AddQuotaFramework(
        typeof(RedisQuotaProfile).Assembly,
        typeof(FunctionAppQuotaProfile).Assembly,
        typeof(WebServiceQuotaProfile).Assembly,
        typeof(PostgresQuotaProfile).Assembly)
    .AddMigrations(assemblies)
    .AddSearchFeature()
    .AddGitProvidersFeature()
    .AddContainerRegistriesFeature()
    .AddSourceBuildsFeature()
    .AddUsersFeature(configuration)
    .AddSettings(configuration)
    .AddHangfireWithMongo(configuration)
    .AddValidatorsFromAssemblies(assemblies)
    .AddOpenApi()
    .AddPoleMediator(assemblies);
builder.Services.AddCors(options =>
{
    var frontendOrigin = configuration["Cors:FrontendOrigin"] ?? "http://localhost:5173";
    options.AddPolicy("frontend-dev", policy =>
        policy.WithOrigins(frontendOrigin)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .WithExposedHeaders(TraceHeaders.CorrelationId, TraceHeaders.TraceId));
});

var app = builder.Build();

await app.Services.GetRequiredService<MigrationRunner>().RunMigrations();

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

app.MapDefaultEndpoints();
app.UseTraceContext();

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseCors("frontend-dev");
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<TenantContextMiddleware>();

app.UseHangfire();

app.MapGitHubCallbackEndpoints();

app.MapGet("/test-keycloak", () => "Hello World!")
    .RequireAuthorization();

app.MapApiEndpoints();

app.Run();

