// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.Reflection;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace Pole.Observability;

/// <summary>
///     Copies the current trace context to MassTransit publish headers.
/// </summary>
public sealed class TraceContextPublishFilter<T> : IFilter<PublishContext<T>>
    where T : class
{
    public async Task Send(PublishContext<T> context, IPipe<PublishContext<T>> next)
    {
        var eventCoreCarrier = EventCoreCarrier.From(context.Message);
        var traceContext = eventCoreCarrier is not null
            ? TraceContext.Create(eventCoreCarrier.TraceId, eventCoreCarrier.CorrelationId)
            : TraceContext.FromCarrierOrCurrent(context.Message as ITraceContextCarrier);

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
///     Restores trace context from MassTransit consume headers or message payload fields.
/// </summary>
public sealed class TraceContextConsumeFilter<T>(
    ILogger<TraceContextConsumeFilter<T>> logger
) : IFilter<ConsumeContext<T>>
    where T : class
{
    public async Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
    {
        var eventCoreCarrier = EventCoreCarrier.From(context.Message);
        var carrier = context.Message as ITraceContextCarrier;
        var traceContext = TraceContext.Create(
            GetHeaderValue(context.Headers, TraceHeaders.TraceId) ??
            eventCoreCarrier?.TraceId ?? carrier?.TraceId ?? TraceContext.Current?.TraceId,
            GetHeaderValue(context.Headers, TraceHeaders.CorrelationId) ?? eventCoreCarrier?.CorrelationId ??
            carrier?.CorrelationId ?? TraceContext.Current?.CorrelationId);

        using var contextScope = TraceContext.Push(traceContext);
        using var loggerScope = logger.BeginScope(
            new Dictionary<string, object>
            {
                ["TraceId"] = traceContext.TraceId,
                ["CorrelationId"] = traceContext.CorrelationId,
            });

        Activity.Current?.SetTag("correlation.id", traceContext.CorrelationId);

        await next.Send(context);
    }

    public void Probe(ProbeContext context)
    {
        context.CreateFilterScope("trace-context-consume");
    }

    private static string? GetHeaderValue(Headers headers, string headerName) =>
        headers.TryGetHeader(headerName, out var value) ? value?.ToString() : null;
}

internal sealed record EventCoreCarrier(
    string? TraceId,
    string? CorrelationId
)
{
    public static EventCoreCarrier? From(object? message)
    {
        var eventCore = message?.GetType()
            .GetProperty("EventCore", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(message);
        if (eventCore is null)
            return null;

        return new EventCoreCarrier(
            eventCore.GetType().GetProperty("TraceId")?.GetValue(eventCore)?.ToString(),
            eventCore.GetType().GetProperty("CorrelationId")?.GetValue(eventCore)?.ToString());
    }
}
