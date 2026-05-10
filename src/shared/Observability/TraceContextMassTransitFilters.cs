using System.Diagnostics;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace Pole.Observability;

/// <summary>
/// Copies the current trace context to MassTransit publish headers.
/// </summary>
public sealed class TraceContextPublishFilter<T> : IFilter<PublishContext<T>>
    where T : class
{
    public async Task Send(PublishContext<T> context, IPipe<PublishContext<T>> next)
    {
        var traceContext = TraceContext.FromCarrierOrCurrent(context.Message as ITraceContextCarrier);

        context.Headers.Set(TraceHeaders.TraceId, traceContext.TraceId);
        context.Headers.Set(TraceHeaders.CorrelationId, traceContext.CorrelationId);

        using var contextScope = TraceContext.Push(traceContext);
        Activity.Current?.SetTag("correlation.id", traceContext.CorrelationId);

        await next.Send(context);
    }

    public void Probe(ProbeContext context)
    {
        context.CreateFilterScope("trace-context-publish");
    }
}

/// <summary>
/// Restores trace context from MassTransit consume headers or message payload fields.
/// </summary>
public sealed class TraceContextConsumeFilter<T>(
    ILogger<TraceContextConsumeFilter<T>> logger)
    : IFilter<ConsumeContext<T>>
    where T : class
{
    public async Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
    {
        var carrier = context.Message as ITraceContextCarrier;
        var traceContext = TraceContext.Create(
            GetHeaderValue(context.Headers, TraceHeaders.TraceId) ?? carrier?.TraceId ?? TraceContext.Current?.TraceId,
            GetHeaderValue(context.Headers, TraceHeaders.CorrelationId) ?? carrier?.CorrelationId ?? TraceContext.Current?.CorrelationId);

        using var contextScope = TraceContext.Push(traceContext);
        using var loggerScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["TraceId"] = traceContext.TraceId,
            ["CorrelationId"] = traceContext.CorrelationId
        });

        Activity.Current?.SetTag("correlation.id", traceContext.CorrelationId);

        await next.Send(context);
    }

    public void Probe(ProbeContext context)
    {
        context.CreateFilterScope("trace-context-consume");
    }

    private static string? GetHeaderValue(Headers headers, string headerName)
        => headers.TryGetHeader(headerName, out var value) ? value?.ToString() : null;
}
