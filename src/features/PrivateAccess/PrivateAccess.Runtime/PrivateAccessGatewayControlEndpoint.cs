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
        app.MapGet("/private-access/gateways", HandleExternalAsync).AllowAnonymous();
        app.MapGet("/_private-access/relay/gateways", HandleInternalAsync).AllowAnonymous();
        return app;
    }

    private static async Task HandleExternalAsync(
        HttpContext context,
        IOptions<PrivateAccessBrokerOptions> configured,
        ResolvePrivateAccessGatewayIdentity resolve,
        RecordPrivateAccessGatewayContact recordContact,
        PrivateAccessBrokerRouting routing,
        PrivateAccessPeerRelay relay,
        PrivateAccessGatewayRegistry registry,
        PrivateAccessSessionCapacity capacity,
        PrivateAccessDrainState drain,
        TimeProvider time)
    {
        if (!configured.Value.Enabled || context.Connection.LocalPort != configured.Value.GatewayPort ||
            !context.Request.IsHttps || !context.WebSockets.IsWebSocketRequest || drain.IsDraining)
        {
            context.Response.StatusCode = 404;
            return;
        }
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        admission.CancelAfter(PrivateAccessLeaseClock.MaximumLease);
        var certificate = await context.Connection.GetClientCertificateAsync(admission.Token);
        if (certificate is null)
        {
            context.Response.StatusCode = 401;
            return;
        }
        var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
        await HandleVerifiedAsync(context, fingerprint, false, resolve, recordContact,
            routing, relay, registry, capacity, drain, time);
    }

    private static async Task HandleInternalAsync(
        HttpContext context,
        ResolvePrivateAccessGatewayIdentity resolve,
        RecordPrivateAccessGatewayContact recordContact,
        PrivateAccessBrokerRouting routing,
        PrivateAccessPeerRelay relay,
        PrivateAccessGatewayRegistry registry,
        PrivateAccessSessionCapacity capacity,
        PrivateAccessDrainState drain,
        TimeProvider time)
    {
        if (!await relay.IsAuthenticatedAsync(context))
        {
            context.Response.StatusCode = 401;
            return;
        }
        var fingerprint = ReadFingerprint(context);
        if (fingerprint is null)
        {
            context.Response.StatusCode = 401;
            return;
        }
        await HandleVerifiedAsync(context, fingerprint, true, resolve, recordContact,
            routing, relay, registry, capacity, drain, time);
    }

    private static async Task HandleVerifiedAsync(
        HttpContext context,
        string fingerprint,
        bool internalRequest,
        ResolvePrivateAccessGatewayIdentity resolve,
        RecordPrivateAccessGatewayContact recordContact,
        PrivateAccessBrokerRouting routing,
        PrivateAccessPeerRelay relay,
        PrivateAccessGatewayRegistry registry,
        PrivateAccessSessionCapacity capacity,
        PrivateAccessDrainState drain,
        TimeProvider time)
    {
        if (drain.IsDraining)
        {
            context.Response.StatusCode = 503;
            return;
        }
        if (!await capacity.Gateways.WaitAsync(0, context.RequestAborted))
        {
            context.Response.StatusCode = 503;
            return;
        }
        try
        {
            var identity = await resolve(fingerprint, context.RequestAborted);
            if (identity is null)
            {
                context.Response.StatusCode = 401;
                return;
            }
            var owner = await routing.ClaimOrFindAsync(identity, context.RequestAborted);
            if (owner is null || owner.Gateway != identity)
            {
                context.Response.StatusCode = 503;
                return;
            }
            if (!routing.IsLocal(owner))
            {
                if (internalRequest)
                    context.Response.StatusCode = 503;
                else
                    await relay.ProxyAsync(context, owner,
                        "/_private-access/relay/gateways", fingerprint: fingerprint);
                return;
            }
            await RunOwnedAsync(context, fingerprint, identity, owner,
                resolve, recordContact, routing, registry, drain, time);
        }
        finally
        {
            capacity.Gateways.Release();
        }
    }

    private static async Task RunOwnedAsync(
        HttpContext context,
        string fingerprint,
        PrivateAccessGatewayIdentity identity,
        PrivateAccessBrokerOwnerLease owner,
        ResolvePrivateAccessGatewayIdentity resolve,
        RecordPrivateAccessGatewayContact recordContact,
        PrivateAccessBrokerRouting routing,
        PrivateAccessGatewayRegistry registry,
        PrivateAccessDrainState drain,
        TimeProvider time)
    {
        PrivateAccessGatewayRegistration? registration = null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, drain.Deadline);
        var monitor = MonitorAsync();
        try
        {
            if (!await routing.IsCurrentAsync(owner, lifetime.Token))
            {
                context.Response.StatusCode = 503;
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var writer = new PrivateAccessFrameWriter(socket);
            while (true)
            {
                lifetime.CancelAfter(PrivateAccessLeaseClock.MaximumLease);
                var frame = await PrivateAccessFrames.ReceiveAsync(socket, lifetime.Token);
                if (frame.Kind != PrivateAccessFrameKind.LeaseChallenge)
                    throw new InvalidDataException("Gateway control requires a lease challenge.");
                var checkedAt = time.GetTimestamp();
                if (await resolve(fingerprint, lifetime.Token) != identity ||
                    !await routing.IsCurrentAsync(owner, lifetime.Token) ||
                    !await recordContact(identity, PrivateAccessGatewayProtocolMetadata.Version,
                        lifetime.Token) ||
                    time.GetElapsedTime(checkedAt) >= PrivateAccessLeaseClock.MaximumLease ||
                    registration?.Disconnected.IsCancellationRequested == true)
                    break;
                if (registration is null)
                    registration = registry.Register(identity, fingerprint, writer, checkedAt, owner);
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
            await lifetime.CancelAsync();
            try { await monitor; }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await routing.ReleaseAsync(owner, cleanup.Token);
            }
        }

        async Task MonitorAsync()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token);
                    if (await routing.RenewAsync(owner, lifetime.Token))
                        continue;
                    registration?.Disconnect();
                    await lifetime.CancelAsync();
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            { }
            catch
            {
                registration?.Disconnect();
                await lifetime.CancelAsync();
                throw;
            }
        }
    }

    internal static string? ReadFingerprint(HttpContext context)
    {
        var value = context.Request.Headers[PrivateAccessPeerRelay.FingerprintHeader].ToString();
        return value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? value
            : null;
    }
}
