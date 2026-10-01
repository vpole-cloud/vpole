using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Primitives;

namespace Pole.Features.PrivateAccess.Runtime;

public static class PrivateAccessStreamEndpoint
{
    public static IEndpointRouteBuilder MapPrivateAccessStreamEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/private-access/streams", HandleAsync).AllowAnonymous();
        return app;
    }

    private static async Task HandleAsync(
        HttpContext context,
        IOptions<PrivateAccessBrokerOptions> options,
        PrivateAccessSessionCapacity capacity,
        PrivateAccessSessionRunner runner)
    {
        if (!options.Value.Enabled || !context.Request.IsHttps || !context.WebSockets.IsWebSocketRequest)
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
        if (!await capacity.Sessions.WaitAsync(0, context.RequestAborted))
        {
            context.Response.StatusCode = 503;
            return;
        }
        try { await runner.RunAsync(context, ticket); }
        finally { capacity.Sessions.Release(); }
    }
}

public sealed class PrivateAccessSessionRunner(
    RedeemPrivateAccessSession redeem,
    RenewPrivateAccessSession renew,
    MarkPrivateAccessSessionStarted markStarted,
    ClosePrivateAccessSession close,
    RecordPrivateAccessWorkerNamespace recordWorkerNamespace,
    ResolveDeploymentTargetConnector resolveConnector,
    PrivateAccessConnectorRegistry connectors,
    PrivateAccessWorkerRendezvous rendezvous,
    PrivateAccessWorkerResources resources,
    IOptions<PrivateAccessBrokerOptions> configured,
    TimeProvider time)
{
    public async Task RunAsync(HttpContext context, string ticket)
    {
        var result = await redeem(ticket, context.RequestAborted);
        if (result is not PrivateAccessLeaseSnapshot lease)
        {
            context.Response.StatusCode = 401;
            return;
        }
        var reason = PrivateAccessSessionCloseReason.SetupFailed;
        var creationAttempted = false;
        var pending = rendezvous.Prepare();
        var settings = configured.Value.WorkerSettings();
        var destination = new PrivateAccessWorkerDestination(lease.TenantId, lease.DeploymentTargetId,
            lease.RuntimeCellId, lease.ResourceDeploymentId, lease.KubernetesNamespace,
            lease.ServiceName, lease.ServicePort, lease.TargetPort);
        try
        {
            var connector = connectors.Find(lease.DeploymentTargetId);
            if (connector is null)
            {
                context.Response.StatusCode = 503;
                return;
            }
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted, connector.Disconnected);
            lifetime.CancelAfter(TimeSpan.FromSeconds(30));
            if (await recordWorkerNamespace(lease.SessionId, settings.Namespace, lifetime.Token)
                is not PrivateAccessWorkerNamespaceRecorded)
            {
                context.Response.StatusCode = 401;
                return;
            }
            creationAttempted = true;
            var created = await resources.CreateAsync(lease.SessionId, destination, settings,
                pending.Ticket, new DateTimeOffset(lease.SessionDeadlineUtc), lifetime.Token);
            if (created is not PrivateAccessWorkerCreated)
            {
                context.Response.StatusCode = 503;
                return;
            }
            using var worker = await pending.Connected.WaitAsync(lifetime.Token);
            using var client = await context.WebSockets.AcceptWebSocketAsync();
            reason = PrivateAccessSessionCloseReason.LeaseDenied;
            reason = await RelayAsync(client, worker, lease.SessionId, connector, lifetime);
        }
        catch (OperationCanceledException)
        {
            if (context.RequestAborted.IsCancellationRequested)
                reason = PrivateAccessSessionCloseReason.ClientDisconnected;
            context.Abort();
        }
        catch (Exception exception) when (exception is IOException or WebSocketException)
        {
            reason = PrivateAccessSessionCloseReason.ConnectorFailure;
            context.Abort();
        }
        finally
        {
            rendezvous.Remove(pending);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await close(lease.SessionId, reason, cleanup.Token); }
            finally
            {
                if (creationAttempted)
                    await resources.DeleteAsync(lease.SessionId, destination, settings.Namespace, cleanup.Token);
            }
        }
    }

    private async Task<PrivateAccessSessionCloseReason> RelayAsync(
        WebSocket client,
        WebSocket worker,
        string sessionId,
        PrivateAccessConnectorRegistration connector,
        CancellationTokenSource lifetime)
    {
        using var toWorker = new PrivateAccessFrameWriter(worker);
        using var toClient = new PrivateAccessFrameWriter(client);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var halfCloses = 0;
        var fromClient = GuardAsync(ClientAsync, lifetime);
        var fromWorker = GuardAsync(WorkerAsync, lifetime);
        try
        {
            var first = await Task.WhenAny(fromClient, fromWorker, complete.Task);
            if (ReferenceEquals(first, complete.Task))
                return PrivateAccessSessionCloseReason.ClientDisconnected;
            return ReferenceEquals(first, fromClient) ? await fromClient : await fromWorker;
        }
        finally
        {
            await lifetime.CancelAsync();
            client.Abort();
            worker.Abort();
            await ObserveAsync(fromClient);
            await ObserveAsync(fromWorker);
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
                        await workerReady.Task.WaitAsync(lifetime.Token);
                        await toWorker.SendAsync(frame.Kind, frame.Payload, lifetime.Token);
                        break;
                    case PrivateAccessFrameKind.HalfClose when !halfClosed:
                        await workerReady.Task.WaitAsync(lifetime.Token);
                        halfClosed = true;
                        await toWorker.SendAsync(frame.Kind, frame.Payload, lifetime.Token);
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

        async Task<PrivateAccessSessionCloseReason> WorkerAsync()
        {
            var halfClosed = false;
            var ready = false;
            while (true)
            {
                var frame = await PrivateAccessFrames.ReceiveAsync(worker, lifetime.Token);
                switch (frame.Kind)
                {
                    case PrivateAccessFrameKind.LeaseChallenge:
                        var began = time.GetTimestamp();
                        if (!connectors.IsCurrent(connector))
                            return PrivateAccessSessionCloseReason.LeaseDenied;
                        var identity = await resolveConnector(connector.Fingerprint, lifetime.Token);
                        if (identity is not DeploymentTargetConnectorIdentity current || current != connector.Identity)
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
                        await toWorker.SendAsync(PrivateAccessFrameKind.LeaseAccepted, frame.Payload, lifetime.Token);
                        break;
                    case PrivateAccessFrameKind.Ready when !ready:
                        if (await markStarted(sessionId, lifetime.Token) is not PrivateAccessSessionStarted)
                            return PrivateAccessSessionCloseReason.LeaseDenied;
                        ready = true;
                        workerReady.TrySetResult();
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
                        throw new InvalidDataException("Unexpected Private Access worker frame.");
                }
            }
        }
    }

    private static async Task<T> GuardAsync<T>(Func<Task<T>> operation, CancellationTokenSource lifetime)
    {
        try { return await operation(); }
        catch
        {
            await lifetime.CancelAsync();
            throw;
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task; }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or WebSocketException)
        {
            // The first terminal pump is observed by RelayAsync; drain its cancelled sibling.
        }
    }
}
