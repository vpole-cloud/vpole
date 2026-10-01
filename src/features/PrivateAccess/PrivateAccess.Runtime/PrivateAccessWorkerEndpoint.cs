using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Pole.Features.PrivateAccess.Runtime;

public static class PrivateAccessWorkerEndpoint
{
    public static IEndpointRouteBuilder MapPrivateAccessWorkerEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/private-access/workers", HandleAsync).AllowAnonymous();
        return app;
    }

    private static async Task HandleAsync(
        HttpContext context,
        IOptions<PrivateAccessBrokerOptions> configured,
        PrivateAccessWorkerRendezvous rendezvous)
    {
        if (!configured.Value.Enabled || !context.Request.IsHttps || !context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 404;
            return;
        }
        var ticket = PrivateAccessTicketHeader.Read(context.Request);
        var worker = ticket is null ? null : rendezvous.Claim(ticket);
        if (worker is null)
        {
            context.Response.StatusCode = 401;
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        if (!worker.Accept(socket))
            return;
        try { await worker.Finished.WaitAsync(context.RequestAborted); }
        finally { socket.Abort(); }
    }
}

internal static class PrivateAccessTicketHeader
{
    public static string? Read(HttpRequest request)
    {
        var values = request.Headers.Authorization;
        if (values.Count != 1 || values[0] is not { } value ||
            !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;
        var token = value[7..];
        return token.Length is > 0 and <= 256 ? token : null;
    }
}
