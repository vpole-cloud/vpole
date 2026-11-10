using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Primitives;

namespace Pole.Features.PrivateAccess.Runtime;

public static class PrivateAccessStreamEndpoint
{
    public static IEndpointRouteBuilder MapPrivateAccessStreamEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/private-access/streams", HandleExternalAsync).AllowAnonymous();
        app.MapGet("/_private-access/relay/streams", HandleInternalAsync).AllowAnonymous();
        return app;
    }

    private static async Task HandleExternalAsync(
        HttpContext context,
        IOptions<PrivateAccessBrokerOptions> options,
        LocatePrivateAccessTicket locate,
        PrivateAccessBrokerRouting routing,
        PrivateAccessPeerRelay relay,
        PrivateAccessGatewayRegistry registry,
        PrivateAccessSessionCapacity capacity,
        PrivateAccessDrainState drain,
        PrivateAccessSessionRunner runner)
    {
        if (!options.Value.Enabled || !context.Request.IsHttps || !context.WebSockets.IsWebSocketRequest ||
            drain.IsDraining)
        {
            context.Response.StatusCode = 404;
            return;
        }

        await HandleVerifiedAsync(context, false, locate, routing, relay, registry, capacity, drain, runner);
    }

    private static async Task HandleInternalAsync(
        HttpContext context,
        LocatePrivateAccessTicket locate,
        PrivateAccessBrokerRouting routing,
        PrivateAccessPeerRelay relay,
        PrivateAccessGatewayRegistry registry,
        PrivateAccessSessionCapacity capacity,
        PrivateAccessDrainState drain,
        PrivateAccessSessionRunner runner)
    {
        if (!await relay.IsAuthenticatedAsync(context))
        {
            context.Response.StatusCode = 401;
            return;
        }

        await HandleVerifiedAsync(context, true, locate, routing, relay, registry, capacity, drain, runner);
    }

    private static async Task HandleVerifiedAsync(
        HttpContext context,
        bool internalRequest,
        LocatePrivateAccessTicket locate,
        PrivateAccessBrokerRouting routing,
        PrivateAccessPeerRelay relay,
        PrivateAccessGatewayRegistry registry,
        PrivateAccessSessionCapacity capacity,
        PrivateAccessDrainState drain,
        PrivateAccessSessionRunner runner)
    {
        if (drain.IsDraining)
        {
            context.Response.StatusCode = 503;
            return;
        }

        var ticket = PrivateAccessTicketHeader.Read(context.Request);
        if (ticket is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        var route = await locate(ticket, context.RequestAborted);
        if (route is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        var owner = await routing.FindAsync(route.TenantId, route.DeploymentTargetId, context.RequestAborted);
        if (owner is null)
        {
            context.Response.StatusCode = 503;
            return;
        }

        if (!routing.IsLocal(owner))
        {
            if (internalRequest)
                context.Response.StatusCode = 503;
            else
                await relay.ProxyAsync(context, owner, "/_private-access/relay/streams", ticket);
            return;
        }

        var gateway = registry.Find(route.TenantId, route.DeploymentTargetId);
        if (gateway is null || gateway.Owner.Epoch != owner.Epoch ||
            !await routing.IsCurrentAsync(owner, context.RequestAborted))
        {
            context.Response.StatusCode = 503;
            return;
        }

        if (!await capacity.Sessions.WaitAsync(0, context.RequestAborted))
        {
            context.Response.StatusCode = 503;
            return;
        }

        try
        {
            await runner.RunAsync(context, ticket, gateway);
        }
        finally
        {
            capacity.Sessions.Release();
        }
    }
}

public sealed class PrivateAccessSessionRunner(
    RedeemPrivateAccessSession redeem,
    RenewPrivateAccessSession renew,
    MarkPrivateAccessSessionStarted markStarted,
    ClosePrivateAccessSession close,
    ResolvePrivateAccessGatewayIdentity resolveGateway,
    PrivateAccessGatewayRegistry gateways,
    PrivateAccessGatewayRendezvous rendezvous,
    PrivateAccessBrokerRouting routing,
    TimeProvider time
)
{
    public async Task RunAsync(
        HttpContext context,
        string ticket,
        PrivateAccessGatewayRegistration gateway)
    {
        var result = await redeem(ticket, context.RequestAborted);
        if (result is not PrivateAccessLeaseSnapshot lease)
        {
            context.Response.StatusCode = 401;
            return;
        }

        var reason = PrivateAccessSessionCloseReason.SetupFailed;
        PendingPrivateAccessGatewayStream? pending = null;
        try
        {
            if (lease.TenantId != gateway.Identity.TenantId ||
                lease.DeploymentTargetId != gateway.Identity.DeploymentTargetId || !gateways.IsCurrent(gateway) ||
                !await routing.IsCurrentAsync(gateway.Owner, context.RequestAborted))
            {
                context.Response.StatusCode = 503;
                return;
            }

            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted,
                gateway.Disconnected);
            lifetime.CancelAfter(TimeSpan.FromSeconds(30));
            var destination = new PrivateAccessGatewayDestination(
                $"{lease.ServiceName}.{lease.KubernetesNamespace}.svc",
                lease.ServicePort.Value);
            pending = rendezvous.Prepare(gateway, destination);
            await gateway.OpenStreamAsync(pending.Ticket, lifetime.Token);
            using var gatewayStream = await pending.Connected.WaitAsync(lifetime.Token);
            using var client = await context.WebSockets.AcceptWebSocketAsync();
            reason = PrivateAccessSessionCloseReason.LeaseDenied;
            reason = await RelayAsync(client, gatewayStream, lease.SessionId, gateway, lifetime);
        }
        catch (OperationCanceledException)
        {
            if (context.RequestAborted.IsCancellationRequested)
                reason = PrivateAccessSessionCloseReason.ClientDisconnected;
            context.Abort();
        }
        catch (Exception exception) when (exception is IOException or WebSocketException)
        {
            reason = PrivateAccessSessionCloseReason.GatewayFailure;
            context.Abort();
        }
        finally
        {
            if (pending is not null)
                rendezvous.Remove(pending);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await close(lease.SessionId, reason, cleanup.Token);
        }
    }

    private async Task<PrivateAccessSessionCloseReason> RelayAsync(
        WebSocket client,
        WebSocket gatewayStream,
        string sessionId,
        PrivateAccessGatewayRegistration gateway,
        CancellationTokenSource lifetime)
    {
        using var toGateway = new PrivateAccessFrameWriter(gatewayStream);
        using var toClient = new PrivateAccessFrameWriter(client);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gatewayReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var halfCloses = 0;
        var fromClient = GuardAsync(ClientAsync, lifetime);
        var fromGateway = GuardAsync(GatewayAsync, lifetime);
        try
        {
            var first = await Task.WhenAny(fromClient, fromGateway, complete.Task);
            if (ReferenceEquals(first, complete.Task))
                return PrivateAccessSessionCloseReason.ClientDisconnected;
            return ReferenceEquals(first, fromClient) ? await fromClient : await fromGateway;
        }
        finally
        {
            await lifetime.CancelAsync();
            client.Abort();
            gatewayStream.Abort();
            await ObserveAsync(fromClient);
            await ObserveAsync(fromGateway);
        }

        async Task<PrivateAccessSessionCloseReason> ClientAsync()
        {
            var halfClosed = false;
            while (true)
            {
                var frame = await PrivateAccessFrames.ReceiveAsync(client, lifetime.Token);
                switch (frame.Kind)
                {
                    case PrivateAccessFrameKind.Data when !halfClosed:
                        await gatewayReady.Task.WaitAsync(lifetime.Token);
                        await toGateway.SendAsync(frame.Kind, frame.Payload, lifetime.Token);
                        break;
                    case PrivateAccessFrameKind.HalfClose when !halfClosed:
                        await gatewayReady.Task.WaitAsync(lifetime.Token);
                        halfClosed = true;
                        await toGateway.SendAsync(frame.Kind, frame.Payload, lifetime.Token);
                        if (Interlocked.Increment(ref halfCloses) == 2)
                            complete.TrySetResult();
                        break;
                    case PrivateAccessFrameKind.Closed:
                        return PrivateAccessSessionCloseReason.ClientDisconnected;
                    default:
                        throw new InvalidDataException("Unexpected Private Access client frame.");
                }
            }
        }

        async Task<PrivateAccessSessionCloseReason> GatewayAsync()
        {
            var halfClosed = false;
            var ready = false;
            while (true)
            {
                var frame = await PrivateAccessFrames.ReceiveAsync(gatewayStream, lifetime.Token);
                switch (frame.Kind)
                {
                    case PrivateAccessFrameKind.LeaseChallenge:
                        var began = time.GetTimestamp();
                        if (!gateways.IsCurrent(gateway) ||
                            !await routing.IsCurrentAsync(gateway.Owner, lifetime.Token))
                            return PrivateAccessSessionCloseReason.LeaseDenied;
                        var identity = await resolveGateway(gateway.Fingerprint, lifetime.Token);
                        if (identity != gateway.Identity)
                            return PrivateAccessSessionCloseReason.LeaseDenied;
                        var renewed = await renew(sessionId, lifetime.Token);
                        if (renewed is not PrivateAccessLeaseSnapshot authorized)
                            return PrivateAccessSessionCloseReason.LeaseDenied;
                        var remaining = new DateTimeOffset(authorized.ExpiresAtUtc) - time.GetUtcNow();
                        var elapsedRemaining = PrivateAccessLeaseClock.MaximumLease - time.GetElapsedTime(began);
                        if (elapsedRemaining < remaining)
                            remaining = elapsedRemaining;
                        if (remaining <= TimeSpan.Zero || lifetime.IsCancellationRequested)
                            return PrivateAccessSessionCloseReason.LeaseDenied;
                        lifetime.CancelAfter(remaining);
                        await toGateway.SendAsync(PrivateAccessFrameKind.LeaseAccepted, frame.Payload, lifetime.Token);
                        break;
                    case PrivateAccessFrameKind.Ready when !ready:
                        if (await markStarted(sessionId, lifetime.Token) is not PrivateAccessSessionStarted)
                            return PrivateAccessSessionCloseReason.LeaseDenied;
                        ready = true;
                        gatewayReady.TrySetResult();
                        break;
                    case PrivateAccessFrameKind.Data when ready && !halfClosed:
                        await toClient.SendAsync(frame.Kind, frame.Payload, lifetime.Token);
                        break;
                    case PrivateAccessFrameKind.HalfClose when ready && !halfClosed:
                        halfClosed = true;
                        await toClient.SendAsync(frame.Kind, frame.Payload, lifetime.Token);
                        if (Interlocked.Increment(ref halfCloses) == 2)
                            complete.TrySetResult();
                        break;
                    default:
                        throw new InvalidDataException("Unexpected Private Access gateway frame.");
                }
            }
        }
    }

    private static async Task<T> GuardAsync<T>(Func<Task<T>> operation, CancellationTokenSource lifetime)
    {
        try
        {
            return await operation();
        }
        catch
        {
            await lifetime.CancelAsync();
            throw;
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or WebSocketException)
        {
            // The first terminal pump is observed by RelayAsync; drain its cancelled sibling.
        }
    }
}
