using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.PrivateAccess.Primitives;

namespace Pole.Features.PrivateAccess.Runtime;

public static class PrivateAccessConnectorEndpoint
{
    public static IEndpointRouteBuilder MapPrivateAccessConnectorEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/private-access/connectors", HandleAsync).AllowAnonymous();
        return app;
    }

    private static async Task HandleAsync(
        HttpContext context,
        IOptions<PrivateAccessBrokerOptions> configured,
        ResolveDeploymentTargetConnector resolve,
        RecordDeploymentTargetConnectorContact recordContact,
        PrivateAccessConnectorRegistry registry,
        PrivateAccessSessionCapacity capacity,
        TimeProvider time)
    {
        if (!configured.Value.Enabled || context.Connection.LocalPort != configured.Value.ConnectorPort ||
            !context.Request.IsHttps || !context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 404;
            return;
        }
        if (!await capacity.Connectors.WaitAsync(0, context.RequestAborted))
        {
            context.Response.StatusCode = 503;
            return;
        }
        PrivateAccessConnectorRegistration? registration = null;
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
            var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
            var checkedAt = time.GetTimestamp();
            if (await resolve(fingerprint, admission.Token) is not DeploymentTargetConnectorIdentity identity)
            {
                context.Response.StatusCode = 401;
                return;
            }
            admission.Token.ThrowIfCancellationRequested();
            registration = registry.Register(identity, fingerprint, checkedAt);
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted, registration.Disconnected);
            while (true)
            {
                lifetime.CancelAfter(PrivateAccessLeaseClock.MaximumLease);
                var frame = await PrivateAccessFrames.ReceiveAsync(socket, lifetime.Token);
                if (frame.Kind != PrivateAccessFrameKind.LeaseChallenge)
                    throw new InvalidDataException("Connector control requires a lease challenge.");
                checkedAt = time.GetTimestamp();
                var current = await resolve(fingerprint, lifetime.Token);
                if (current is not DeploymentTargetConnectorIdentity found || found != identity)
                    break;
                var contact = await recordContact(new DeploymentTargetConnectorContact(
                    identity.TargetId, identity.ConnectorId, identity.Generation,
                    "worker-v1", "Healthy", ["worker-v1"], time.GetUtcNow().UtcDateTime), lifetime.Token);
                if (contact is not DeploymentTargetConnectorContactRecorded ||
                    time.GetElapsedTime(checkedAt) >= PrivateAccessLeaseClock.MaximumLease ||
                    !registry.IsCurrent(registration))
                    break;
                registration.Authorized(checkedAt);
                await PrivateAccessFrames.SendAsync(socket, PrivateAccessFrameKind.LeaseAccepted,
                    frame.Payload, lifetime.Token);
            }
            socket.Abort();
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or WebSocketException)
        {
            context.Abort();
        }
        finally
        {
            if (registration is not null)
                registry.Remove(registration);
            capacity.Connectors.Release();
        }
    }
}
