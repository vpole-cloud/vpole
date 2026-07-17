using Pole.Functions.Sdk;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddPoleFunctions(options =>
    {
        options.FunctionAppId = builder.Configuration["PoleFunctions:FunctionAppId"] ?? "sample-app";
        options.RevisionId = builder.Configuration["PoleFunctions:RevisionId"] ?? "local";
        options.MaxConcurrencyPerInstance = 8;
        options.DefaultHandlerConcurrencyPerInstance = 4;
        options.AllowUnauthenticatedAgentRequests = builder.Environment.IsDevelopment();
    })
    .AddHttpHandler(
        "hello",
        (request, context, cancellationToken) =>
        {
            var name = request.QueryString.TrimStart('?');
            var message = string.IsNullOrWhiteSpace(name)
                ? "Hello from Pole Functions!"
                : $"Hello, {name}!";
            return ValueTask.FromResult(PoleHttpResponse.Text(message));
        })
    .AddTimerHandler(
        "heartbeat",
        (timer, context, cancellationToken) =>
        {
            Console.WriteLine(
                "Heartbeat {OccurrenceId} scheduled for {ScheduledForUtc:O}",
                timer.OccurrenceId,
                timer.ScheduledForUtc);
            return ValueTask.CompletedTask;
        });

var app = builder.Build();
app.MapPoleFunctionsAgent();
app.Run();
