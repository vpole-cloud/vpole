using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Pole.Tests.E2E.Infrastructure;

/// <summary>Test-only TLS pass-through for a dedicated target. Does not inspect credentials or modify TLS.</summary>
internal sealed class KubernetesApiRelay : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> sessions = new();
    private readonly ConcurrentBag<Task> bridges = [];
    private readonly Uri upstream;
    private readonly Task accepting;
    private volatile bool paused;
    public Uri Endpoint { get; }

    public KubernetesApiRelay(Uri upstream)
    {
        this.upstream = upstream;
        if (upstream.Host is not ("127.0.0.1" or "localhost")) throw new InvalidOperationException("The test relay requires a loopback lab endpoint.");
        listener.Start();
        Endpoint = new UriBuilder(upstream) { Port = ((IPEndPoint)listener.LocalEndpoint).Port }.Uri;
        accepting = AcceptAsync();
    }
    public void Pause()
    {
        paused = true;
        foreach (var source in sessions.Values)
        {
            try { source.Cancel(); }
            catch (ObjectDisposedException) { } // A bridge can finish while this snapshot is canceled.
        }
    }
    public void Resume() => paused = false;
    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var incoming = await listener.AcceptTcpClientAsync(lifetime.Token);
                if (paused) { incoming.Dispose(); continue; }
                bridges.Add(BridgeAsync(incoming));
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (SocketException) when (lifetime.IsCancellationRequested) { }
    }
    private async Task BridgeAsync(TcpClient incoming)
    {
        using (incoming)
        using (var outgoing = new TcpClient())
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            var id = Guid.NewGuid(); sessions[id] = linked;
            try
            {
                if (paused) linked.Cancel();
                await outgoing.ConnectAsync(upstream.Host, upstream.Port, linked.Token);
                var from = incoming.GetStream(); var to = outgoing.GetStream();
                var upload = from.CopyToAsync(to, linked.Token); var download = to.CopyToAsync(from, linked.Token);
                await Task.WhenAny(upload, download);
                await linked.CancelAsync(); incoming.Close(); outgoing.Close();
                try { await Task.WhenAll(upload, download); }
                catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            finally { sessions.TryRemove(id, out _); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync(); listener.Stop();
        await accepting; await Task.WhenAll(bridges);
        lifetime.Dispose();
    }
}
