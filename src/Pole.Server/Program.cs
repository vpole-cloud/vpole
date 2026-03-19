using System.Reflection;
using Pole.Feature.DomainNames.Common;
using Pole.Features.Outbox;
using Pole.Server.Extensions;
using Pole.ServiceDefaults;
using Pole.Features.Postgres.Endpoints;
using Pole.Features.RedisServices.Common;
using Pole.Features.ResourceGroups.Common;
using Pole.Features.WebServices.Common;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
Assembly[] assemblies = [
    typeof(WebServiceEndpoints).Assembly,
    typeof(PostgresClusterEndpoints).Assembly,
    typeof(DomainNameEndpoints).Assembly,
    typeof(OutboxProcessorJob).Assembly,
    typeof(ResourceGroupEndpoints).Assembly,
    typeof(RedisServiceEndpoints).Assembly,
];

builder.AddServiceDefaults();
builder.AddMongoDBClient("pole-database");
builder.AddSecurity();

builder.Services.AddKubernetes(configuration)
    .AddEventBus(configuration, assemblies)
    .AddDatabase()
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
