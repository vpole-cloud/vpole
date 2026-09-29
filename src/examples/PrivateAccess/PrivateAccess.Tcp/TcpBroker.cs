using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;

namespace Pole.PrivateAccess.Prototype.Tcp;

public sealed class TcpBroker : IPrivateAccessTransport
{
    private sealed record WaitingConnector(
        Wire Wire,
        TaskCompletionSource Started,
        TaskCompletionSource Finished);

    private readonly X509Certificate2 certificate;
    private readonly IReadOnlyDictionary<string, PeerIdentity> peers;
    private readonly PrivateAccessCoordinator coordinator;
    private readonly TimeSpan leaseDuration;
    private readonly ConcurrentDictionary<string, Channel<WaitingConnector>> connectors = new();
    private readonly TcpListener listener;
    private readonly SemaphoreSlim clientSlots = new(32);
    private readonly SemaphoreSlim connectorSlots = new(32);

    public TcpBroker(
        IPEndPoint endpoint,
        X509Certificate2 certificate,
        IReadOnlyDictionary<string, PeerIdentity> peers,
        IPrivateAccessAuthority authority,
        TimeSpan leaseDuration)
    {
        this.certificate = certificate;
        this.peers = peers;
        this.leaseDuration = leaseDuration;
        using var validation = new LeaseGuard(leaseDuration);
        coordinator = new PrivateAccessCoordinator(authority, this);
        listener = new TcpListener(endpoint);
        listener.Start(64);
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public async Task RunAsync(CancellationToken ct)
    {
        var active = new List<Task>();
        using var slots = new SemaphoreSlim(64);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await slots.WaitAsync(ct);
                var socket = await listener.AcceptTcpClientAsync(ct);
                active.RemoveAll(task => task.IsCompletedSuccessfully);
                active.Add(ServeAsync(socket, slots, ct));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            listener.Stop();
            await Task.WhenAll(active);
        }
    }

    private async Task ServeAsync(TcpClient socket, SemaphoreSlim slots, CancellationToken ct)
    {
        using (socket)
        {
            var handshakeSlotHeld = true;
            SemaphoreSlim? roleSlots = null;
            try
            {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handshake.CancelAfter(TimeSpan.FromSeconds(5));
                using var tls = new SslStream(socket.GetStream(), false, (_, remote, _, _) =>
                    remote is not null && peers.ContainsKey(TlsIdentity.Pin(remote)) &&
                    TlsIdentity.Matches(remote, TlsIdentity.Pin(remote)));
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls13
                }, handshake.Token);
                var peer = peers[TlsIdentity.Pin(tls.RemoteCertificate!)];
                await using var wire = new Wire(tls);
                var hello = await wire.ReceiveAsync(handshake.Token);
                roleSlots = peer is ConnectorPeer ? connectorSlots : clientSlots;
                if (!await roleSlots.WaitAsync(0, handshake.Token))
                {
                    roleSlots = null;
                    return;
                }
                slots.Release();
                handshakeSlotHeld = false;
                switch (peer)
                {
                    case ConnectorPeer connector when hello.Kind == FrameKind.Connector:
                        var registration = Wire.Decode<ConnectorHello>(hello);
                        if (registration.TargetId != connector.TargetId)
                            return;
                        var queue = connectors.GetOrAdd(connector.TargetId, _ => Channel.CreateBounded<WaitingConnector>(8));
                        var waiting = new WaitingConnector(wire, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                        if (!queue.Writer.TryWrite(waiting))
                            return;
                        try
                        {
                            await waiting.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
                            await waiting.Finished.Task.WaitAsync(ct);
                        }
                        finally { waiting.Finished.TrySetResult(); }
                        break;
                    case UserPeer user when hello.Kind == FrameKind.Client:
                        var request = Wire.Decode<AccessRequest>(hello);
                        using (var admission = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            admission.CancelAfter(TimeSpan.FromSeconds(5));
                            var outcome = await coordinator.OpenAsync(user.Identity, request, admission.Token);
                            if (outcome is AccessOpened opened)
                            {
                                await using var target = opened.Channel;
                                await wire.SendAsync(FrameKind.Ready, ReadOnlyMemory<byte>.Empty, ct);
                                await using var client = new FramedChannel(wire, RejectControlAsync, ct);
                                await RelayAsync(client, target, ct);
                            }
                        }
                        break;
                }
            }
            catch (Exception error) when (error is InvalidDataException or IOException or SocketException or AuthenticationException or
                OperationCanceledException or ObjectDisposedException or System.Text.Json.JsonException or TimeoutException)
            {
                // Fail closed. Prototype deliberately emits no payloads, certificates or grant contents.
            }
            finally
            {
                if (handshakeSlotHeld)
                    slots.Release();
                roleSlots?.Release();
            }
        }
    }

    public async Task<PrivateAccessChannel> OpenAsync(AccessAllowed access, RenewAccess renew, CancellationToken ct)
    {
        var queue = connectors.GetOrAdd(access.Binding.TargetId, _ => Channel.CreateBounded<WaitingConnector>(8));
        WaitingConnector waiting;
        do
        {
            waiting = await queue.Reader.ReadAsync(ct);
        } while (waiting.Finished.Task.IsCompleted);
        waiting.Started.TrySetResult();
        // Admission cancellation must not become the established session's lifetime.
        var lease = new CancellationTokenSource(leaseDuration);
        FramedChannel? channel = null;
        try
        {
            await waiting.Wire.SendJsonAsync(FrameKind.Open, access.Binding, ct);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            channel = new FramedChannel(waiting.Wire, HandleControlAsync, lease.Token);
            await ready.Task.WaitAsync(ct);
            return new LeasedChannel(channel, lease, waiting.Finished);

            async Task HandleControlAsync(Frame frame, CancellationToken token)
            {
                switch (frame.Kind)
                {
                    case FrameKind.Challenge:
                        var challenge = Wire.Decode<LeaseChallenge>(frame);
                        var outcome = await renew(token).AsTask().WaitAsync(token);
                        if (outcome is not AccessAllowed allowed)
                            throw new InvalidDataException("Authorization no longer valid.");
                        var duration = Math.Min(leaseDuration.TotalMilliseconds, (allowed.ExpiresAt - DateTimeOffset.UtcNow).TotalMilliseconds);
                        if (duration < 1)
                            throw new InvalidDataException("Grant expired.");
                        lease.CancelAfter(TimeSpan.FromMilliseconds(duration));
                        await waiting.Wire.SendJsonAsync(FrameKind.Lease, new LeaseReply(challenge.Nonce, (int)duration), token);
                        break;
                    case FrameKind.Ready when frame.Payload.Length == 0 && !ready.Task.IsCompleted:
                        ready.SetResult();
                        break;
                    default:
                        throw new InvalidDataException("Unexpected connector message.");
                }
            }
        }
        catch
        {
            await lease.CancelAsync();
            try
            {
                if (channel is not null)
                    await channel.DisposeAsync();
            }
            finally
            {
                lease.Dispose();
                waiting.Finished.TrySetResult();
            }
            throw;
        }
    }

    internal static Task RejectControlAsync(Frame _, CancellationToken __) =>
        Task.FromException(new InvalidDataException("Unexpected control message."));

    private static async Task RelayAsync(PrivateAccessChannel first, PrivateAccessChannel second, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await Task.WhenAll(CopyAsync(first, second), CopyAsync(second, first));

        async Task CopyAsync(PrivateAccessChannel source, PrivateAccessChannel destination)
        {
            try
            {
                var buffer = new byte[Wire.MaximumPayload];
                int count;
                while ((count = await source.ReadAsync(buffer, lifetime.Token)) > 0)
                    await destination.WriteAsync(buffer.AsMemory(0, count), lifetime.Token);
                await destination.CompleteWritesAsync(lifetime.Token);
            }
            catch
            {
                await lifetime.CancelAsync();
                throw;
            }
        }
    }

    private sealed class LeasedChannel(
        FramedChannel inner,
        CancellationTokenSource lease,
        TaskCompletionSource finished) : PrivateAccessChannel
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
            inner.WriteAsync(buffer, cancellationToken);
        public override ValueTask CompleteWritesAsync(CancellationToken cancellationToken) =>
            inner.CompleteWritesAsync(cancellationToken);
        public override async ValueTask DisposeAsync()
        {
            try { await inner.DisposeAsync(); }
            finally
            {
                lease.Dispose();
                finished.TrySetResult();
            }
        }
    }
}
