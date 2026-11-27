// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

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
        app.MapGet("/private-access/gateway-streams", HandleExternalAsync).AllowAnonymous();
        app.MapGet("/_private-access/relay/gateway-streams", HandleInternalAsync).AllowAnonymous();
        return app;
    }

    private static async Task HandleExternalAsync(
        HttpContext context,
        IOptions<PrivateAccessBrokerOptions> configured,
        ResolvePrivateAccessGatewayIdentity resolve,
        PrivateAccessBrokerRouting routing,
        PrivateAccessPeerRelay relay,
        PrivateAccessGatewayRegistry registry,
        PrivateAccessGatewayRendezvous rendezvous)
    {
        if (!configured.Value.Enabled || context.Connection.LocalPort != configured.Value.GatewayPort ||
            !context.Request.IsHttps || !context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 404;
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
        await HandleVerifiedAsync(context, fingerprint, false, resolve, routing, relay, registry, rendezvous);
    }

    private static async Task HandleInternalAsync(
        HttpContext context,
        ResolvePrivateAccessGatewayIdentity resolve,
        PrivateAccessBrokerRouting routing,
        PrivateAccessPeerRelay relay,
        PrivateAccessGatewayRegistry registry,
        PrivateAccessGatewayRendezvous rendezvous)
    {
        if (!await relay.IsAuthenticatedAsync(context))
        {
            context.Response.StatusCode = 401;
            return;
        }

        var fingerprint = PrivateAccessGatewayControlEndpoint.ReadFingerprint(context);
        if (fingerprint is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        await HandleVerifiedAsync(context, fingerprint, true, resolve, routing, relay, registry, rendezvous);
    }

    private static async Task HandleVerifiedAsync(
        HttpContext context,
        string fingerprint,
        bool internalRequest,
        ResolvePrivateAccessGatewayIdentity resolve,
        PrivateAccessBrokerRouting routing,
        PrivateAccessPeerRelay relay,
        PrivateAccessGatewayRegistry registry,
        PrivateAccessGatewayRendezvous rendezvous)
    {
        var ticket = PrivateAccessTicketHeader.Read(context.Request);
        if (ticket is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        var gateway = await resolve(fingerprint, context.RequestAborted);
        if (gateway is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        var owner = await routing.FindAsync(gateway.TenantId, gateway.DeploymentTargetId, context.RequestAborted);
        if (owner is null || owner.Gateway != gateway)
        {
            context.Response.StatusCode = 503;
            return;
        }

        if (!routing.IsLocal(owner))
        {
            if (internalRequest)
                context.Response.StatusCode = 503;
            else
                await relay.ProxyAsync(context, owner, "/_private-access/relay/gateway-streams", ticket, fingerprint);
            return;
        }

        var registration = registry.Find(gateway.TenantId, gateway.DeploymentTargetId);
        if (registration is null || registration.Identity != gateway || registration.Fingerprint != fingerprint ||
            registration.Owner.Epoch != owner.Epoch || !await routing.IsCurrentAsync(owner, context.RequestAborted))
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
                context.RequestAborted,
                registration.Disconnected);
            await PrivateAccessGatewayProtocol.SendDestinationAsync(socket, pending.Destination, lifetime.Token);
            if (!pending.Accept(socket))
                return;
            await pending.Finished.WaitAsync(lifetime.Token);
            socket.Abort();
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or WebSocketException)
        {
            context.Abort();
        }
        finally
        {
            rendezvous.Remove(pending);
        }
    }
}
