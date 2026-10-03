using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Pole.Features.PrivateAccess.Primitives;

namespace Pole.Cli.Forwarding;

public sealed class LocalForwarder(
    string tenantId,
    string grantId,
    IPrivateAccessConnectionFactory connectionFactory)
{
    private const int MaximumConnections = 128;

    public async Task RunAsync(int localPort, CancellationToken cancellationToken, Action<IPEndPoint>? onListening = null)
    {
        if (localPort is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(localPort), "Port must be between 0 and 65535.");

        using var listener = new TcpListener(IPAddress.Loopback, localPort);
        listener.Start(MaximumConnections);
        onListening?.Invoke((IPEndPoint)listener.LocalEndpoint);

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var slots = new SemaphoreSlim(MaximumConnections);
        var active = new Dictionary<long, (TcpClient Client, Task Task)>();
        long nextId = 0;

        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await ReapCompletedConnectionsAsync(active);
                await slots.WaitAsync(lifetime.Token);
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(lifetime.Token);
                }
                catch
                {
                    slots.Release();
                    throw;
                }

                var id = Interlocked.Increment(ref nextId);
                var task = ServeAsync(client, slots, lifetime.Token);
                active.Add(id, (client, task));
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            lifetime.Cancel();
            listener.Stop();
            foreach (var connection in active.Values)
                connection.Client.Dispose();
            await Task.WhenAll(active.Values.Select(connection => connection.Task));
        }
    }

    private static async Task ReapCompletedConnectionsAsync(
        Dictionary<long, (TcpClient Client, Task Task)> active)
    {
        foreach (var (id, connection) in active.Where(pair => pair.Value.Task.IsCompleted).ToArray())
        {
            await connection.Task;
            connection.Client.Dispose();
            active.Remove(id);
        }
    }

    private async Task ServeAsync(TcpClient localClient, SemaphoreSlim slots, CancellationToken cancellationToken)
    {
        using var clientLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IPrivateAccessConnection? connection = null;
        Task? localToRemote = null;
        Task<bool>? remoteToLocal = null;
        var completedNormally = false;
        try
        {
            localClient.NoDelay = true;
            connection = await connectionFactory.OpenAsync(tenantId, grantId, clientLifetime.Token);
            localToRemote = PumpLocalToRemoteAsync(localClient, connection, clientLifetime.Token);
            remoteToLocal = PumpRemoteToLocalAsync(localClient, connection, clientLifetime.Token);
            completedNormally = await PumpUntilBothDirectionsCloseAsync(
                localClient,
                clientLifetime,
                localToRemote,
                remoteToLocal);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error) when (error is HttpRequestException or IOException or SocketException or WebSocketException or InvalidOperationException)
        {
            // A failed session closes only this local connection. Never log tickets or payloads.
        }
        finally
        {
            try
            {
                await CleanupConnectionAsync(
                    localClient,
                    clientLifetime,
                    connection,
                    localToRemote,
                    remoteToLocal,
                    completedNormally);
            }
            finally
            {
                slots.Release();
            }
        }
    }

    private static async Task<bool> PumpUntilBothDirectionsCloseAsync(
        TcpClient localClient,
        CancellationTokenSource lifetime,
        Task localToRemote,
        Task<bool> remoteToLocal)
    {
        var first = await Task.WhenAny(localToRemote, remoteToLocal);
        if (ReferenceEquals(first, localToRemote))
        {
            await localToRemote;
            _ = await remoteToLocal;
            return true;
        }

        var remoteClosed = await remoteToLocal;
        if (remoteClosed)
        {
            lifetime.Cancel();
            localClient.Dispose();
            return true;
        }

        await localToRemote;
        return true;
    }

    private static async Task CleanupConnectionAsync(
        TcpClient localClient,
        CancellationTokenSource lifetime,
        IPrivateAccessConnection? connection,
        Task? localToRemote,
        Task<bool>? remoteToLocal,
        bool completedNormally)
    {
        if (!completedNormally)
        {
            lifetime.Cancel();
            localClient.Dispose();
            connection?.Abort();
        }

        if (localToRemote is not null && remoteToLocal is not null)
        {
            try
            {
                await Task.WhenAll(localToRemote, remoteToLocal);
            }
            catch (Exception)
            {
                // The terminal pump failure was handled by ServeAsync. Drain both tasks before disposal.
            }
        }

        try
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
        }
        finally
        {
            localClient.Dispose();
        }
    }

    private static async Task PumpLocalToRemoteAsync(
        TcpClient localClient,
        IPrivateAccessConnection connection,
        CancellationToken cancellationToken)
    {
        var stream = localClient.GetStream();
        var buffer = new byte[PrivateAccessFrames.MaximumPayloadLength];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                await connection.CompleteWritesAsync(cancellationToken);
                return;
            }
            await connection.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
    }

    private static async Task<bool> PumpRemoteToLocalAsync(
        TcpClient localClient,
        IPrivateAccessConnection connection,
        CancellationToken cancellationToken)
    {
        var stream = localClient.GetStream();
        var buffer = new byte[PrivateAccessFrames.MaximumPayloadLength];
        while (true)
        {
            var result = await connection.ReadAsync(buffer, cancellationToken);
            if (result.BytesRead == 0)
            {
                localClient.Client.Shutdown(SocketShutdown.Send);
                return result.RemoteClosed;
            }
            await stream.WriteAsync(buffer.AsMemory(0, result.BytesRead), cancellationToken);
        }
    }
}
