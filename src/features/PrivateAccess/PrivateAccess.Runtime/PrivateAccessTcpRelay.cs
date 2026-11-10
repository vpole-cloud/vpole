using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading.Channels;
using Pole.Features.PrivateAccess.Primitives;

namespace Pole.Features.PrivateAccess.Runtime;

/// <summary>
///     One gateway stream accepts one server-issued endpoint and one transport. It has no routing
///     API, listener, cluster credentials, or fallback destination.
/// </summary>
public sealed class PrivateAccessTcpRelay(
    TimeProvider time
)
{
    public async Task RunAsync(
        WebSocket broker,
        string serviceDns,
        int servicePort,
        CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var writer = new PrivateAccessFrameWriter(broker);
        var lease = new PrivateAccessLeaseClock(time);
        var initialChallenge = lease.Challenge();
        var watchdog = WatchLeaseAsync(lease, lifetime);
        using var backend = new TcpClient();
        Task? fromBackend = null;
        Task? fromBroker = null;
        Task? renewal = null;
        try
        {
            await writer.SendAsync(PrivateAccessFrameKind.LeaseChallenge, initialChallenge, lifetime.Token);
            var firstReply = await PrivateAccessFrames.ReceiveAsync(broker, lifetime.Token);
            if (firstReply.Kind != PrivateAccessFrameKind.LeaseAccepted || !lease.Accept(firstReply.Payload.Span))
                throw new InvalidDataException("Private Access admission was not confirmed.");

            // No database connection before a fresh challenge-bound authority decision.
            await backend.ConnectAsync(serviceDns, servicePort, lifetime.Token);
            await writer.SendAsync(PrivateAccessFrameKind.Ready, ReadOnlyMemory<byte>.Empty, lifetime.Token);
            var stream = backend.GetStream();
            var replies = Channel.CreateBounded<bool>(1);
            fromBackend = GuardAsync(() => ReadBackendAsync(stream, writer, lifetime.Token), lifetime);
            fromBroker = GuardAsync(
                () => ReadBrokerAsync(broker, stream, backend, lease, replies.Writer, lifetime.Token),
                lifetime);
            renewal = RenewAsync(writer, lease, replies.Reader, lifetime.Token);
            var data = Task.WhenAll(fromBackend, fromBroker);
            var first = await Task.WhenAny(data, renewal, watchdog);
            await first;
            if (first != data)
                throw new IOException("Private Access authorization lease ended.");
        }
        finally
        {
            await lifetime.CancelAsync();
            backend.Dispose();
            broker.Abort();
            await ObserveAsync(fromBackend);
            await ObserveAsync(fromBroker);
            await ObserveAsync(renewal);
            await ObserveAsync(watchdog);
        }
    }

    private async Task WatchLeaseAsync(PrivateAccessLeaseClock lease, CancellationTokenSource lifetime)
    {
        while (!lifetime.IsCancellationRequested)
        {
            var remaining = lease.Remaining;
            if (remaining <= TimeSpan.Zero)
            {
                await lifetime.CancelAsync();
                return;
            }

            await Task.Delay(remaining, time, lifetime.Token);
        }
    }

    private async Task RenewAsync(
        PrivateAccessFrameWriter writer,
        PrivateAccessLeaseClock lease,
        ChannelReader<bool> replies,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(PrivateAccessLeaseClock.RenewalInterval, time, cancellationToken);
            await writer.SendAsync(PrivateAccessFrameKind.LeaseChallenge, lease.Challenge(), cancellationToken);
            await replies.ReadAsync(cancellationToken);
        }
    }

    private static async Task ReadBackendAsync(
        NetworkStream backend,
        PrivateAccessFrameWriter writer,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[PrivateAccessFrames.MaximumPayloadLength];
        while (true)
        {
            var length = await backend.ReadAsync(buffer, cancellationToken);
            if (length == 0)
            {
                await writer.SendAsync(PrivateAccessFrameKind.HalfClose, ReadOnlyMemory<byte>.Empty, cancellationToken);
                return;
            }

            await writer.SendAsync(PrivateAccessFrameKind.Data, buffer.AsMemory(0, length), cancellationToken);
        }
    }

    private static async Task ReadBrokerAsync(
        WebSocket broker,
        NetworkStream backend,
        TcpClient client,
        PrivateAccessLeaseClock lease,
        ChannelWriter<bool> replies,
        CancellationToken cancellationToken)
    {
        var writeClosed = false;
        while (true)
        {
            var frame = await PrivateAccessFrames.ReceiveAsync(broker, cancellationToken);
            switch (frame.Kind)
            {
                case PrivateAccessFrameKind.Data when !writeClosed:
                    await backend.WriteAsync(frame.Payload, cancellationToken);
                    break;
                case PrivateAccessFrameKind.HalfClose when !writeClosed:
                    writeClosed = true;
                    client.Client.Shutdown(SocketShutdown.Send);
                    break;
                case PrivateAccessFrameKind.Closed:
                    return;
                case PrivateAccessFrameKind.LeaseAccepted:
                    if (!lease.Accept(frame.Payload.Span) || !replies.TryWrite(true))
                        throw new InvalidDataException("Unexpected Private Access lease response.");
                    break;
                default:
                    throw new InvalidDataException("Unexpected Private Access gateway stream frame.");
            }
        }
    }

    private static async Task ObserveAsync(Task? task)
    {
        if (task is null)
            return;
        try
        {
            await task;
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or WebSocketException
                                              or SocketException or InvalidDataException)
        {
            // The caller observes the operation that caused shutdown. Sibling pumps
            // are drained so neither transport nor lease tasks outlive their session.
        }
    }

    private static async Task GuardAsync(Func<Task> operation, CancellationTokenSource lifetime)
    {
        try
        {
            await operation();
        }
        catch
        {
            await lifetime.CancelAsync();
            throw;
        }
    }
}
