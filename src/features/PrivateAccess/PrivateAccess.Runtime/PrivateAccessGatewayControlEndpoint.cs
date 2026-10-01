using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Primitives;

namespace Pole.Features.PrivateAccess.Runtime;

public static class PrivateAccessGatewayControlEndpoint
{
    public static IEndpointRouteBuilder MapPrivateAccessGatewayControlEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/private-access/gateways", HandleAsync).AllowAnonymous();
        return app;
    }

    private static async Task HandleAsync(
        HttpContext context,
        IOptions<PrivateAccessBrokerOptions> configured,
        ResolvePrivateAccessGatewayIdentity resolve,
        RecordPrivateAccessGatewayContact recordContact,
        PrivateAccessGatewayRegistry registry,
        PrivateAccessSessionCapacity capacity,
        TimeProvider time)
    {
        if (!configured.Value.Enabled || context.Connection.LocalPort != configured.Value.GatewayPort ||
            !context.Request.IsHttps || !context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 404;
            return;
        }
        if (!await capacity.Gateways.WaitAsync(0, context.RequestAborted))
        {
            context.Response.StatusCode = 503;
            return;
        }
        PrivateAccessGatewayRegistration? registration = null;
        try
        {
            using var admission = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            admission.CancelAfter(PrivateAccessLeaseClock.MaximumLease);
            var certificate = await context.Connection.GetClientCertificateAsync(admission.Token);
            if (certificate is null)
            {
                context.Response.StatusCode = 401;
                return;
            }
            var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
            var identity = await resolve(fingerprint, admission.Token);
            if (identity is null)
            {
                context.Response.StatusCode = 401;
                return;
            }
            admission.Token.ThrowIfCancellationRequested();
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var writer = new PrivateAccessFrameWriter(socket);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            while (true)
            {
                lifetime.CancelAfter(PrivateAccessLeaseClock.MaximumLease);
                var frame = await PrivateAccessFrames.ReceiveAsync(socket, lifetime.Token);
                if (frame.Kind != PrivateAccessFrameKind.LeaseChallenge)
                    throw new InvalidDataException("Gateway control requires a lease challenge.");
                var checkedAt = time.GetTimestamp();
                if (await resolve(fingerprint, lifetime.Token) != identity ||
                    !await recordContact(identity, "gateway-v1", lifetime.Token) ||
                    time.GetElapsedTime(checkedAt) >= PrivateAccessLeaseClock.MaximumLease ||
                    registration?.Disconnected.IsCancellationRequested == true)
                    break;
                if (registration is null)
                    registration = registry.Register(identity, fingerprint, writer, checkedAt);
                else
                    registration.Authorized(checkedAt);
                await writer.SendAsync(PrivateAccessFrameKind.LeaseAccepted,
                    frame.Payload, lifetime.Token);
                registration.ControlAccepted();
            }
            socket.Abort();
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or
            WebSocketException or InvalidDataException)
        {
            context.Abort();
        }
        finally
        {
            if (registration is not null)
                registry.Remove(registration);
            capacity.Gateways.Release();
        }
    }
}
