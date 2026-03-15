using FluentValidation;
using Pole.Features;
using Pole.Server.Extensions;
using Pole.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var assembly = typeof(Program).Assembly;

builder.AddServiceDefaults();
builder.AddMongoDBClient("pole-database");
builder.AddSecurity();

builder.Services.AddKubernetes(configuration)
    .AddEventBus(configuration, assembly)
    .AddDatabase()
    .AddSettings(configuration)
    .AddHangfireWithMongo(configuration)
    .AddValidatorsFromAssembly(assembly)
    .AddOpenApi()
    .AddPoleMediator(configuration, assembly);
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
