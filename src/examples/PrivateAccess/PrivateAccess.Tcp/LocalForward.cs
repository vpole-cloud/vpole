using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Pole.PrivateAccess.Prototype.Tcp;

public sealed class LocalForward(
    string brokerHost,
    int brokerPort,
    X509Certificate2 certificate,
    string brokerPin,
    AccessRequest request)
{
    public int? ListeningPort { get; private set; }

    public async Task RunAsync(int localPort, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, localPort);
        listener.Start(16);
        ListeningPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var active = new List<Task>();
        using var slots = new SemaphoreSlim(16);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await slots.WaitAsync(ct);
                var client = await listener.AcceptTcpClientAsync(ct);
                active.RemoveAll(task => task.IsCompletedSuccessfully);
                active.Add(ServeAsync(client));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            listener.Stop();
            ListeningPort = null;
            await Task.WhenAll(active);
        }

        async Task ServeAsync(TcpClient socket)
        {
            using (socket)
            {
                try
                {
                    await using var channel = await OpenAsync(ct);
                    await TcpPump.RunAsync(socket, channel, ct);
                }
                catch (Exception error) when (error is InvalidDataException or IOException or SocketException or OperationCanceledException or
                    ObjectDisposedException or System.Security.Authentication.AuthenticationException)
                {
                    // Closing this local TCP connection represents failed admission or an ended session.
                }
                finally { slots.Release(); }
            }
        }
    }

    public async Task<PrivateAccessChannel> OpenAsync(CancellationToken ct)
    {
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshake.CancelAfter(TimeSpan.FromSeconds(5));
        var wire = await TlsIdentity.ConnectAsync(brokerHost, brokerPort, certificate, brokerPin, handshake.Token);
        try
        {
            await wire.SendJsonAsync(FrameKind.Client, request, handshake.Token);
            var response = await wire.ReceiveAsync(handshake.Token);
            if (response.Kind != FrameKind.Ready || response.Payload.Length != 0)
                throw new InvalidDataException("Session was not admitted.");
            return new FramedChannel(wire, TcpBroker.RejectControlAsync, ct);
        }
        catch
        {
            await wire.DisposeAsync();
            throw;
        }
    }
}
