using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Pole.Features.Functions.Runtime.Gateway;

public static class FunctionGatewayEndpointMapping
{
    private static readonly HashSet<string> Methods = new(
        [
            HttpMethods.Get,
            HttpMethods.Post,
            HttpMethods.Put,
            HttpMethods.Patch,
            HttpMethods.Delete,
            HttpMethods.Head,
            HttpMethods.Options,
        ],
        StringComparer.OrdinalIgnoreCase);

    public static async Task DispatchAsync(HttpContext context)
    {
        if (!Methods.Contains(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = string.Join(", ", Methods);
            return;
        }

        if (!context.Request.Path.StartsWithSegments("/functions", out var remaining))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var path = (remaining.Value ?? "").TrimStart('/');
        if (path.Length == 0)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var separator = path.IndexOf('/');
        var functionRoute = separator < 0 ? path : path[..separator];
        var handlerPath = separator < 0 ? "" : path[(separator + 1)..];
        var gateway = context.RequestServices.GetService<IFunctionHttpGateway>();
        if (gateway is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(
                new FunctionGatewayError("function_runtime_unavailable", "The Function runtime is not configured."),
                context.RequestAborted);
            return;
        }

        await gateway.InvokeAsync(context, functionRoute, handlerPath, context.RequestAborted);
    }
}
