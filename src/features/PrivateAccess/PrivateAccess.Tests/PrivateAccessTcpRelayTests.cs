using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Pole.Features.PrivateAccess.Primitives;
using Pole.Features.PrivateAccess.Runtime;
using Xunit;

namespace Pole.Features.PrivateAccess.Tests;

public sealed class PrivateAccessTcpRelayTests
{
    [Fact]
    public async Task FreshAdmission_PrecedesDial_AndHalfClosePreservesTheResponse()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var backend = new TcpListener(IPAddress.Loopback, 0);
        backend.Start();
        await using var transport = await WebSocketPair.CreateAsync(cancellation.Token);
        var worker = new PrivateAccessTcpRelay(TimeProvider.System).RunAsync(transport.Worker,
            "127.0.0.1", ((IPEndPoint)backend.LocalEndpoint).Port, cancellation.Token);
        var challenge = await PrivateAccessFrames.ReceiveAsync(transport.Broker, cancellation.Token);
        Assert.Equal(PrivateAccessFrameKind.LeaseChallenge, challenge.Kind);
        Assert.False(backend.Pending());
        await PrivateAccessFrames.SendAsync(transport.Broker, PrivateAccessFrameKind.LeaseAccepted,
            challenge.Payload, cancellation.Token);
        using var accepted = await backend.AcceptTcpClientAsync(cancellation.Token);
        var ready = await PrivateAccessFrames.ReceiveAsync(transport.Broker, cancellation.Token);
        Assert.Equal(PrivateAccessFrameKind.Ready, ready.Kind);
        await PrivateAccessFrames.SendAsync(transport.Broker, PrivateAccessFrameKind.Data,
            Encoding.UTF8.GetBytes("request"), cancellation.Token);
        await PrivateAccessFrames.SendAsync(transport.Broker, PrivateAccessFrameKind.HalfClose,
            ReadOnlyMemory<byte>.Empty, cancellation.Token);
        var stream = accepted.GetStream();
        var received = new byte[7];
        await stream.ReadExactlyAsync(received, cancellation.Token);
        Assert.Equal("request", Encoding.UTF8.GetString(received));
        Assert.Equal(0, await stream.ReadAsync(new byte[1], cancellation.Token));
        await stream.WriteAsync(Encoding.UTF8.GetBytes("response"), cancellation.Token);
        accepted.Client.Shutdown(SocketShutdown.Send);
        var reply = await PrivateAccessFrames.ReceiveAsync(transport.Broker, cancellation.Token);
        Assert.Equal(PrivateAccessFrameKind.Data, reply.Kind);
        Assert.Equal("response", Encoding.UTF8.GetString(reply.Payload.Span));
        Assert.Equal(PrivateAccessFrameKind.HalfClose,
            (await PrivateAccessFrames.ReceiveAsync(transport.Broker, cancellation.Token)).Kind);
        await PrivateAccessFrames.SendAsync(transport.Broker, PrivateAccessFrameKind.Closed,
            ReadOnlyMemory<byte>.Empty, cancellation.Token);
        await worker;
    }

    [Fact]
    public async Task ForgedInitialLease_DoesNotDialTheDatabase()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var backend = new TcpListener(IPAddress.Loopback, 0);
        backend.Start();
        await using var transport = await WebSocketPair.CreateAsync(cancellation.Token);
        var worker = new PrivateAccessTcpRelay(TimeProvider.System).RunAsync(transport.Worker,
            "127.0.0.1", ((IPEndPoint)backend.LocalEndpoint).Port, cancellation.Token);
        _ = await PrivateAccessFrames.ReceiveAsync(transport.Broker, cancellation.Token);
        await PrivateAccessFrames.SendAsync(transport.Broker, PrivateAccessFrameKind.LeaseAccepted,
            new byte[32], cancellation.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => worker);
        Assert.False(backend.Pending());
    }

    [Fact]
    public async Task FragmentedFrame_IsReassembled_ButOversizedMessageIsRejected()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var transport = await WebSocketPair.CreateAsync(cancellation.Token);
        await transport.Broker.SendAsync(new byte[] { 1, 65 }.AsMemory(), WebSocketMessageType.Binary,
            false, cancellation.Token);
        await transport.Broker.SendAsync(new byte[] { 66 }.AsMemory(), WebSocketMessageType.Binary,
            true, cancellation.Token);
        var frame = await PrivateAccessFrames.ReceiveAsync(transport.Worker, cancellation.Token);
        Assert.Equal("AB", Encoding.UTF8.GetString(frame.Payload.Span));
        var tooLarge = new byte[PrivateAccessFrames.MaximumPayloadLength + 2];
        tooLarge[0] = 1;
        await transport.Broker.SendAsync(tooLarge.AsMemory(), WebSocketMessageType.Binary, true,
            cancellation.Token);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await PrivateAccessFrames.ReceiveAsync(transport.Worker, cancellation.Token));
    }

    [Fact]
    public async Task DataFlow_CannotKeepAnUnrenewedLeaseAlive()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var backend = new TcpListener(IPAddress.Loopback, 0);
        backend.Start();
        await using var transport = await WebSocketPair.CreateAsync(cancellation.Token);
        var worker = new PrivateAccessTcpRelay(TimeProvider.System).RunAsync(transport.Worker,
            "127.0.0.1", ((IPEndPoint)backend.LocalEndpoint).Port, cancellation.Token);
        var challenge = await PrivateAccessFrames.ReceiveAsync(transport.Broker, cancellation.Token);
        await PrivateAccessFrames.SendAsync(transport.Broker, PrivateAccessFrameKind.LeaseAccepted,
            challenge.Payload, cancellation.Token);
        using var accepted = await backend.AcceptTcpClientAsync(cancellation.Token);
        Assert.Equal(PrivateAccessFrameKind.Ready,
            (await PrivateAccessFrames.ReceiveAsync(transport.Broker, cancellation.Token)).Kind);
        var stream = accepted.GetStream();
        var observedBytes = 0;
        var discardRenewals = DrainAsync();
        var sending = SendAsync();
        var stopped = await Record.ExceptionAsync(() => worker);
        Assert.NotNull(stopped);
        Assert.True(observedBytes > 5);
        Assert.False(cancellation.IsCancellationRequested);
        await sending;
        await discardRenewals;

        async Task SendAsync()
        {
            try
            {
                while (!worker.IsCompleted)
                {
                    await stream.WriteAsync(new byte[] { 42 }, cancellation.Token);
                    await Task.Delay(200, cancellation.Token);
                }
            }
            catch (IOException) { }
        }

        async Task DrainAsync()
        {
            try
            {
                while (true)
                {
                    var frame = await PrivateAccessFrames.ReceiveAsync(transport.Broker, cancellation.Token);
                    if (frame.Kind == PrivateAccessFrameKind.Data)
                        observedBytes += frame.Payload.Length;
                    // Deliberately black-hole lease challenges while continuing payload traffic.
                }
            }
            catch (Exception error) when (error is WebSocketException or IOException) { }
        }
    }

    private sealed class WebSocketPair(
        TcpClient workerClient,
        TcpClient brokerClient,
        WebSocket worker,
        WebSocket broker) : IAsyncDisposable
    {
        public WebSocket Worker { get; } = worker;
        public WebSocket Broker { get; } = broker;

        public static async Task<WebSocketPair> CreateAsync(CancellationToken cancellationToken)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var workerClient = new TcpClient();
            await workerClient.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port,
                cancellationToken);
            var brokerClient = await listener.AcceptTcpClientAsync(cancellationToken);
            return new WebSocketPair(workerClient, brokerClient,
                WebSocket.CreateFromStream(workerClient.GetStream(), false, null, Timeout.InfiniteTimeSpan),
                WebSocket.CreateFromStream(brokerClient.GetStream(), true, null, Timeout.InfiniteTimeSpan));
        }

        public ValueTask DisposeAsync()
        {
            Worker.Dispose();
            Broker.Dispose();
            workerClient.Dispose();
            brokerClient.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
