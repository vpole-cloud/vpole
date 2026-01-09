using k8s;
using MassTransit;
using Pole.Server.Api;
using Pole.Server.Mediator;
using Pole.ServiceDefaults;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddOpenApi();

builder.Services.AddSingleton<IKubernetes>(_ =>
{
    var config = KubernetesClientConfiguration.BuildConfigFromConfigFile(currentContext: "docker-desktop");
    return new Kubernetes(config);
});

// find and register all Handlers
var assembly = typeof(Program).Assembly;
var handlerTypes = assembly.GetTypes()
    .Where(t => t is { IsClass: true, IsAbstract: false } && t.GetInterfaces()
        .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IMediatorHandler<,>)));

foreach (var handlerType in handlerTypes)
{
    var interfaceType = handlerType.GetInterfaces()
        .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IMediatorHandler<,>));
    builder.Services.AddScoped(interfaceType, handlerType);
}

builder.AddMongoDBClient("pole-mongo");
builder.AddRabbitMQClient("pole-rabbitmq");
// add mass transit
builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host("rabbitmq", "/", h =>
        {
            h.Username("guest");
            h.Password("guest");
        });
    });
});

var app = builder.Build();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options.Title = "My API";
        options.Theme = ScalarTheme.Default;
    });
}

app.UseHttpsRedirection();

app.MapApiEndpoints();


app.Run();
