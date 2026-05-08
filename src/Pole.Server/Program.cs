using System.Reflection;
using Pole.Database;
using Pole.Feature.DomainNames.Common;
using Pole.Features.Outbox;
using Pole.Features.Postgres.Endpoints;
using Pole.Features.Postgres.Events;
using Pole.Features.RedisServices.Common;
using Pole.Features.RedisServices.Events;
using Pole.Features.ResourceGroups.Common;
using Pole.Features.ResourceGroups.Events;
using Pole.Features.Search.Common;
using Pole.Features.Search.Services;
using Pole.Features.WebServices.Common;
using Pole.Features.WebServices.Events;
using Pole.Server.Extensions;
using Pole.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
Assembly[] assemblies = [
    typeof(WebServiceEndpoints).Assembly,
    typeof(PostgresClusterEndpoints).Assembly,
    typeof(DnsRecordEndpoints).Assembly,
    typeof(OutboxProcessorJob).Assembly,
    typeof(ResourceGroupEndpoints).Assembly,
    typeof(RedisServiceEndpoints).Assembly,
    typeof(SearchEndpoints).Assembly,
];
Assembly[] outboxEventAssemblies = [
    typeof(Pole.Features.WebServices.Events.CreateEvent).Assembly,
    typeof(Pole.Features.Postgres.Events.CreateEvent).Assembly,
    typeof(Pole.Features.RedisServices.Events.CreateEvent).Assembly,
    typeof(Pole.Features.ResourceGroups.Events.CreateEvent).Assembly
];

builder.AddServiceDefaults();
builder.AddMongoDBClient("pole-database");
builder.AddSecurity();

builder.Services.AddKubernetes(configuration)
    .AddEventBus(configuration, assemblies)
    .AddOutboxEventAssemblies(outboxEventAssemblies)
    .AddDatabase()
    .AddMigrations(assemblies)
    .AddSearchFeature()
    .AddSettings(configuration)
    .AddHangfireWithMongo(configuration)
    .AddValidatorsFromAssemblies(assemblies)
    .AddOpenApi()
    .AddPoleMediator(assemblies);
builder.Services.AddCors(options =>
{
    options.AddPolicy("frontend-dev", policy =>
        policy.WithOrigins("http://localhost:5173")
            .AllowAnyHeader()
            .AllowAnyMethod());
});

var app = builder.Build();

await app.Services.GetRequiredService<MigrationRunner>().RunMigrations();

app.MapDefaultEndpoints();

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseCors("frontend-dev");
app.UseAuthentication();
app.UseAuthorization();

app.UseHangfire();

app.MapGet("/test-keycloak", () => "Hello World!")
    .RequireAuthorization();

app.MapApiEndpoints();

app.Run();