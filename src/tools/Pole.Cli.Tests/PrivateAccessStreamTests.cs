using Pole.Cli.Api;
using Pole.Cli.Forwarding;
using System.Net.WebSockets;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Pole.Cli.Tests;

public sealed class PrivateAccessStreamTests
{
    [Fact]
    public async Task Every_physical_connection_gets_a_fresh_session_ticket()
    {
        var sessionSource = new CountingSessionSource();
        var transport = new CapturingTransport();
        var factory = new AuthorizedPrivateAccessConnectionFactory(
            new Uri("https://pole.example"),
            sessionSource,
            transport);

        await using var first = await factory.OpenAsync("tenant-1", "grant-1", CancellationToken.None);
        await using var second = await factory.OpenAsync("tenant-1", "grant-1", CancellationToken.None);

        Assert.Equal(2, sessionSource.Calls);
        Assert.Equal(new[] { "ticket-1", "ticket-2" }, transport.Tickets);
    }

    [Fact]
    public async Task Client_connection_preserves_half_close_and_rejects_worker_lease_frames()
    {
        var socket = new ScriptedWebSocket(
        [
            [1, .. Encoding.UTF8.GetBytes("response")],
            [2]
        ]);
        await using var connection = new WebSocketPrivateAccessConnection(socket);
        var buffer = new byte[32];

        var data = await connection.ReadAsync(buffer, CancellationToken.None);
        Assert.Equal("response", Encoding.UTF8.GetString(buffer, 0, data.BytesRead));
        var halfClose = await connection.ReadAsync(buffer, CancellationToken.None);
        Assert.Equal(0, halfClose.BytesRead);
        Assert.False(halfClose.RemoteClosed);
        await connection.WriteAsync(Encoding.UTF8.GetBytes("request-tail"), CancellationToken.None);
        await connection.CompleteWritesAsync(CancellationToken.None);
        Assert.Collection(socket.SentMessages,
            dataFrame => Assert.Equal((byte)1, dataFrame[0]),
            halfCloseFrame => Assert.Equal((byte)2, halfCloseFrame[0]));

        var closeSocket = new ScriptedWebSocket([[3]]);
        await using var closeConnection = new WebSocketPrivateAccessConnection(closeSocket);
        var closed = await closeConnection.ReadAsync(buffer, CancellationToken.None);
        Assert.True(closed.RemoteClosed);

        var leaseSocket = new ScriptedWebSocket([[4, .. new byte[32]]]);
        await using var leaseConnection = new WebSocketPrivateAccessConnection(leaseSocket);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await leaseConnection.ReadAsync(buffer, CancellationToken.None));
    }

    [Fact]
    public async Task Client_connection_rejects_oversized_writes_and_honors_read_cancellation()
    {
        var socket = new ScriptedWebSocket([]);
        await using var connection = new WebSocketPrivateAccessConnection(socket);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await connection.WriteAsync(new byte[(16 * 1024) + 1], CancellationToken.None));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await connection.ReadAsync(new byte[1], cancellation.Token));
    }

    [Fact]
    public void WebSocket_endpoint_keeps_the_configured_https_origin_and_rejects_downgrade()
    {
        Assert.Equal(
            "wss://pole.example:8443/private-access/streams",
            WebSocketPrivateAccessStreamTransport.GetEndpoint(new Uri("https://pole.example:8443/")).AbsoluteUri);
        Assert.Throws<InvalidOperationException>(() =>
            WebSocketPrivateAccessStreamTransport.GetEndpoint(new Uri("http://127.0.0.1:8080/")));
    }

    [Fact]
    public async Task Stream_handler_does_not_forward_ticket_to_redirect_origin()
    {
        using var source = new HttpListener();
        using var destination = new HttpListener();
        var sourcePort = ReservePort();
        var destinationPort = ReservePort();
        source.Prefixes.Add($"http://127.0.0.1:{sourcePort}/");
        destination.Prefixes.Add($"http://127.0.0.1:{destinationPort}/");
        source.Start();
        destination.Start();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var http = new HttpClient(WebSocketPrivateAccessStreamTransport.CreateNoRedirectHandler());
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{sourcePort}/private-access/streams");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "one-use-ticket");
        var destinationRequest = destination.GetContextAsync();
        var responseTask = http.SendAsync(request, cancellation.Token);
        var context = await source.GetContextAsync().WaitAsync(cancellation.Token);
        using (context.Response)
        {
            context.Response.StatusCode = (int)HttpStatusCode.Redirect;
            context.Response.RedirectLocation = $"http://127.0.0.1:{destinationPort}/attacker";
            context.Response.ContentLength64 = 0;
            context.Response.Close();
        }

        using var response = await responseTask;
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await Task.Delay(150, cancellation.Token);
        Assert.False(destinationRequest.IsCompleted);
        destination.Stop();
        await Assert.ThrowsAnyAsync<Exception>(async () => await destinationRequest);
    }

    [Fact]
    public async Task Forwarder_binds_only_loopback_and_authorizes_each_accepted_socket()
    {
        var factory = new CountingConnectionFactory();
        var forwarder = new LocalForwarder("tenant-from-cli", "grant-from-cli", factory);
        using var cancellation = new CancellationTokenSource();
        var listening = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = forwarder.RunAsync(0, cancellation.Token, endpoint => listening.TrySetResult(endpoint));
        var endpoint = await listening.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(IPAddress.Loopback, endpoint.Address);
        for (var index = 0; index < 2; index++)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, endpoint.Port);
            client.Client.Shutdown(SocketShutdown.Send);
        }

        await factory.TwoConnections.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, factory.Opened.Count);
        Assert.All(factory.Opened, item =>
        {
            Assert.Equal("tenant-from-cli", item.TenantId);
            Assert.Equal("grant-from-cli", item.GrantId);
        });

        cancellation.Cancel();
        await run;
    }

    [Fact]
    public async Task Forwarder_keeps_backend_response_open_after_local_client_half_close()
    {
        var backend = new EchoAfterEofConnection();
        var forwarder = new LocalForwarder("tenant-1", "grant-1", new SingleConnectionFactory(backend));
        using var cancellation = new CancellationTokenSource();
        var listening = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = forwarder.RunAsync(0, cancellation.Token, endpoint => listening.TrySetResult(endpoint));
        var endpoint = await listening.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, endpoint.Port);
        var local = client.GetStream();
        await local.WriteAsync(Encoding.UTF8.GetBytes("request-before-eof"));
        client.Client.Shutdown(SocketShutdown.Send);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var response = new MemoryStream();
        var buffer = new byte[64];
        while (true)
        {
            var count = await local.ReadAsync(buffer, timeout.Token);
            if (count == 0)
                break;
            response.Write(buffer, 0, count);
        }

        Assert.Equal("request-before-eof", Encoding.UTF8.GetString(backend.RequestBytes));
        Assert.Equal("backend-response-after-eof", Encoding.UTF8.GetString(response.ToArray()));
        cancellation.Cancel();
        await run;
    }

    [Fact]
    public async Task Forwarder_cancels_blocked_local_read_when_transport_read_fails()
    {
        var factory = new SingleConnectionFactory(new FailingReadConnection());
        var forwarder = new LocalForwarder("tenant-1", "grant-1", factory);
        using var cancellation = new CancellationTokenSource();
        var listening = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = forwarder.RunAsync(0, cancellation.Token, endpoint => listening.TrySetResult(endpoint));
        var endpoint = await listening.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, endpoint.Port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var read = client.GetStream().ReadAsync(new byte[1], timeout.Token).AsTask();

        Assert.Equal(0, await read);
        cancellation.Cancel();
        await run;
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class CountingSessionSource : IPrivateAccessSessionSource
    {
        public int Calls { get; private set; }

        public Task<PrivateAccessSessionDto> CreateSessionAsync(string tenantId, string grantId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new PrivateAccessSessionDto($"session-{Calls}", $"ticket-{Calls}", DateTimeOffset.UtcNow.AddSeconds(30)));
        }
    }

    private sealed class CapturingTransport : IPrivateAccessStreamTransport
    {
        public List<string> Tickets { get; } = [];

        public Task<IPrivateAccessConnection> OpenAsync(Uri server, string ticket, CancellationToken cancellationToken)
        {
            Assert.Equal("https://pole.example", server.GetLeftPart(UriPartial.Authority));
            Tickets.Add(ticket);
            return Task.FromResult<IPrivateAccessConnection>(new EmptyConnection());
        }
    }

    private sealed class CountingConnectionFactory : IPrivateAccessConnectionFactory
    {
        private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(string TenantId, string GrantId)> Opened { get; } = [];
        public Task TwoConnections => completed.Task;

        public Task<IPrivateAccessConnection> OpenAsync(string tenantId, string grantId, CancellationToken cancellationToken)
        {
            Opened.Add((tenantId, grantId));
            if (Opened.Count == 2)
                completed.TrySetResult();
            return Task.FromResult<IPrivateAccessConnection>(new EmptyConnection());
        }
    }

    private sealed class SingleConnectionFactory(IPrivateAccessConnection connection) : IPrivateAccessConnectionFactory
    {
        public Task<IPrivateAccessConnection> OpenAsync(string tenantId, string grantId, CancellationToken cancellationToken) =>
            Task.FromResult(connection);
    }

    private sealed class EchoAfterEofConnection : IPrivateAccessConnection
    {
        private readonly MemoryStream request = new();
        private readonly TaskCompletionSource<byte[]> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool responseSent;
        public byte[] RequestBytes => request.ToArray();

        public ValueTask<PrivateAccessReadResult> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (responseSent)
                return ValueTask.FromResult(new PrivateAccessReadResult(0, true));
            return ReadResponseAsync(buffer, cancellationToken);
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            request.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public ValueTask CompleteWritesAsync(CancellationToken cancellationToken)
        {
            response.TrySetResult(Encoding.UTF8.GetBytes("backend-response-after-eof"));
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Abort() => response.TrySetCanceled();

        private async ValueTask<PrivateAccessReadResult> ReadResponseAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var payload = await response.Task.WaitAsync(cancellationToken);
            payload.AsMemory().CopyTo(buffer);
            responseSent = true;
            return new PrivateAccessReadResult(payload.Length, false);
        }
    }

    private sealed class FailingReadConnection : IPrivateAccessConnection
    {
        public ValueTask<PrivateAccessReadResult> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            ValueTask.FromException<PrivateAccessReadResult>(new IOException("Synthetic transport read failure."));
        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask CompleteWritesAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public void Abort() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyConnection : IPrivateAccessConnection
    {
        public ValueTask<PrivateAccessReadResult> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PrivateAccessReadResult(0, true));
        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask CompleteWritesAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public void Abort() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedWebSocket(byte[][] received) : WebSocket
    {
        private int nextMessage;
        private WebSocketState state = WebSocketState.Open;
        public List<byte[]> SentMessages { get; } = [];
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;

        public override void Abort() => state = WebSocketState.Aborted;
        public override void Dispose() => state = WebSocketState.Closed;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            state = WebSocketState.Closed;
            return Task.CompletedTask;
        }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (nextMessage == received.Length)
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            var payload = received[nextMessage++];
            if (payload.Length > buffer.Count)
                throw new InvalidOperationException("Test frame exceeds receive buffer.");
            payload.CopyTo(buffer.AsSpan());
            return Task.FromResult(new WebSocketReceiveResult(payload.Length, WebSocketMessageType.Binary, true));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(WebSocketMessageType.Binary, messageType);
            Assert.True(endOfMessage);
            SentMessages.Add(buffer.ToArray());
            return Task.CompletedTask;
        }
    }
}
