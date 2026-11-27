// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Pole.Observability;

/// <summary>
///     Establishes trace and correlation IDs for an HTTP request and returns them as response headers.
/// </summary>
public sealed class TraceContextMiddleware(
    RequestDelegate next,
    ILogger<TraceContextMiddleware> logger
)
{
    public async Task InvokeAsync(HttpContext httpContext)
    {
        var correlationId = TraceContext.NormalizeCorrelationId(
            GetFirstHeaderValue(httpContext.Request.Headers[TraceHeaders.CorrelationId]));
        var traceContext = TraceContext.Create(Activity.Current?.TraceId.ToString(), correlationId);

        using var contextScope = TraceContext.Push(traceContext);
        using var loggerScope = logger.BeginScope(
            new Dictionary<string, object>
            {
                ["TraceId"] = traceContext.TraceId,
                ["CorrelationId"] = traceContext.CorrelationId,
            });

        Activity.Current?.SetTag("correlation.id", traceContext.CorrelationId);
        SetResponseHeaders(httpContext, traceContext);

        httpContext.Response.OnStarting(
            static state =>
            {
                var (context, snapshot) = ((HttpContext, TraceContextSnapshot))state;
                SetResponseHeaders(context, snapshot);
                return Task.CompletedTask;
            },
            (httpContext, traceContext));

        await next(httpContext);
    }

    private static string? GetFirstHeaderValue(StringValues values) => values.Count > 0 ? values[0] : null;

    private static void SetResponseHeaders(HttpContext httpContext, TraceContextSnapshot traceContext)
    {
        httpContext.Response.Headers[TraceHeaders.CorrelationId] = traceContext.CorrelationId;
        httpContext.Response.Headers[TraceHeaders.TraceId] = traceContext.TraceId;
    }
}

public static class TraceContextMiddlewareExtensions
{
    /// <summary>
    ///     Adds HTTP trace context extraction, logging scope enrichment, and response header propagation.
    /// </summary>
    public static IApplicationBuilder UseTraceContext(this IApplicationBuilder app) =>
        app.UseMiddleware<TraceContextMiddleware>();
}
