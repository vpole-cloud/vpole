using FluentValidation;
using Pole.Server.Extensions;
using Pole.Server.Features;
using Pole.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var assembly = typeof(Program).Assembly;

builder.AddServiceDefaults();
builder.AddMongoDBClient("pole-mongo");
builder.AddSecurity();

builder.Services.AddKubernetes(configuration)
    .AddEventBus(configuration, assembly)
    .AddDatabase()
    .AddValidatorsFromAssembly(assembly)
    .AddOpenApi()
    .AddPoleMediator(configuration, assembly);

var app = builder.Build();

app.MapDefaultEndpoints()
    .UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/qwer", () => "Hello World!")
    .RequireAuthorization();

app.MapApiEndpoints();

app.Run();
