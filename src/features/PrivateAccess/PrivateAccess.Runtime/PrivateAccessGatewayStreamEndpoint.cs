using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Pole.Features.PrivateAccess.Contracts;

namespace Pole.Features.PrivateAccess.Runtime;

public static class PrivateAccessGatewayStreamEndpoint
{
    public static IEndpointRouteBuilder MapPrivateAccessGatewayStreamEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/private-access/gateway-streams", HandleAsync).AllowAnonymous();
        return app;
    }

    private static async Task HandleAsync(
        HttpContext context,
        IOptions<PrivateAccessBrokerOptions> configured,
        ResolvePrivateAccessGatewayIdentity resolve,
        PrivateAccessGatewayRegistry registry,
        PrivateAccessGatewayRendezvous rendezvous)
    {
        if (!configured.Value.Enabled || context.Connection.LocalPort != configured.Value.GatewayPort ||
            !context.Request.IsHttps || !context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 404;
            return;
        }
        var ticket = PrivateAccessTicketHeader.Read(context.Request);
        if (ticket is null)
        {
            context.Response.StatusCode = 401;
            return;
        }
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        admission.CancelAfter(TimeSpan.FromSeconds(10));
        var certificate = await context.Connection.GetClientCertificateAsync(admission.Token);
        if (certificate is null)
        {
            context.Response.StatusCode = 401;
            return;
        }
        var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
        var identity = await resolve(fingerprint, admission.Token);
        var registration = identity is null
            ? null
            : registry.Find(identity.TenantId, identity.DeploymentTargetId);
        if (registration is null || registration.Identity != identity ||
            registration.Fingerprint != fingerprint || !registry.IsCurrent(registration))
        {
            context.Response.StatusCode = 401;
            return;
        }
        var pending = rendezvous.Claim(ticket, registration);
        if (pending is null)
        {
            context.Response.StatusCode = 401;
            return;
        }
        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted, registration.Disconnected);
            await PrivateAccessGatewayProtocol.SendDestinationAsync(
                socket, pending.Destination, lifetime.Token);
            if (!pending.Accept(socket))
                return;
            await pending.Finished.WaitAsync(lifetime.Token);
            socket.Abort();
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or
            WebSocketException)
        {
            context.Abort();
        }
        finally
        {
            rendezvous.Remove(pending);
        }
    }
}
