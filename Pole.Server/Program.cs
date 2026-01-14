using FluentValidation;
using Pole.Server.Api;
using Pole.Server.Extensions;
using Pole.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var assembly = typeof(Program).Assembly;

builder.AddServiceDefaults();
builder.AddMongoDBClient("pole-mongo");

builder.Services.AddKubernetes(configuration)
    .AddEventBus(configuration, assembly)
    .AddDatabase()
    .AddValidatorsFromAssembly(assembly)
    .AddOpenApi()
    .AddPoleMediator(configuration, assembly);

var app = builder.Build();

app.MapDefaultEndpoints()
    .UseHttpsRedirection();

app.MapApiEndpoints();

app.Run();
