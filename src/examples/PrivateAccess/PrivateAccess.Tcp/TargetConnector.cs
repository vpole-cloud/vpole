using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Pole.PrivateAccess.Prototype.Tcp;

public sealed record EndpointKey(
    string TenantId,
    string ResourceDeploymentId,
    string EndpointId);

public sealed record TcpDestination(
    string Host,
    int Port);

public sealed class TargetConnector(
    string brokerHost,
    int brokerPort,
    X509Certificate2 certificate,
    string brokerPin,
    string targetId,
    IReadOnlyDictionary<EndpointKey, TcpDestination> endpoints,
    TimeSpan leaseDuration)
{
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(ct);
            }
            catch (Exception error) when (error is InvalidDataException or IOException or SocketException or AuthenticationException or
                OperationCanceledException or ObjectDisposedException or System.Text.Json.JsonException or TimeoutException)
            {
                // The next attempt creates a new authenticated channel and fresh authorization.
            }
            try { await Task.Delay(100, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }
    }

    private async Task RunSessionAsync(CancellationToken ct)
    {
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshake.CancelAfter(TimeSpan.FromSeconds(5));
        await using var wire = await TlsIdentity.ConnectAsync(brokerHost, brokerPort, certificate, brokerPin, handshake.Token);
        await wire.SendJsonAsync(FrameKind.Connector, new ConnectorHello(targetId), handshake.Token);
        var open = await wire.ReceiveAsync(ct);
        if (open.Kind != FrameKind.Open)
            throw new InvalidDataException("Expected an endpoint binding.");
        var binding = Wire.Decode<AccessBinding>(open);
        if (binding.TargetId != targetId)
            throw new InvalidDataException("Foreign target binding.");
        var key = new EndpointKey(binding.Identity.TenantId, binding.Request.ResourceDeploymentId, binding.Request.EndpointId);
        if (!endpoints.TryGetValue(key, out var destination))
            throw new InvalidDataException("Unknown endpoint binding.");

        using var lease = new LeaseGuard(leaseDuration);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.Expired);
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var channel = new FramedChannel(wire, ReceiveLeaseAsync, lifetime.Token);
        var renewal = RenewAsync();
        try
        {
            // Never dial the destination on the initial Open alone: require a fresh online renewal.
            await reply.Task.WaitAsync(lifetime.Token);
            using var target = new TcpClient();
            await target.ConnectAsync(destination.Host, destination.Port, lifetime.Token);
            await wire.SendAsync(FrameKind.Ready, ReadOnlyMemory<byte>.Empty, lifetime.Token);
            await TcpPump.RunAsync(target, channel, lifetime.Token);
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await renewal; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }

        Task ReceiveLeaseAsync(Frame frame, CancellationToken _)
        {
            if (frame.Kind != FrameKind.Lease)
                throw new InvalidDataException("Expected a lease reply.");
            lease.Accept(Wire.Decode<LeaseReply>(frame));
            reply.TrySetResult();
            return Task.CompletedTask;
        }

        async Task RenewAsync()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    await wire.SendJsonAsync(FrameKind.Challenge, lease.Challenge(), lifetime.Token);
                    await reply.Task.WaitAsync(lifetime.Token);
                    await Task.Delay(leaseDuration / 3, lifetime.Token);
                    reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
            catch
            {
                await lifetime.CancelAsync();
                throw;
            }
        }
    }
}
