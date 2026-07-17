using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Pole.Features.Functions.Runtime.Gateway;

public sealed class FunctionListenerIsolationMiddleware(
    RequestDelegate next,
    FunctionGatewayOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var isFunctionPath = context.Request.Path.StartsWithSegments("/functions");
        var isFunctionPort = context.Connection.LocalPort == options.PublicPort ||
                             context.Connection.LocalPort == options.InternalPort;
        if (isFunctionPort != isFunctionPath)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context);
    }
}

public static class FunctionListenerIsolationExtensions
{
    public static IApplicationBuilder UseFunctionListenerIsolation(this IApplicationBuilder app) =>
        app.UseMiddleware<FunctionListenerIsolationMiddleware>();
}
