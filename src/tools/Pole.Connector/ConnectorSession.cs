using System.Net.WebSockets;
using System.Security.Authentication;
using Pole.Features.PrivateAccess.Primitives;

namespace Pole.Connector;

public static class ConnectorSession
{
    public static async Task RunAsync(
        BrokerOrigin origin,
        string stateDirectory,
        CancellationToken cancellationToken)
    {
        var store = ConnectorIdentityStore.Open(stateDirectory);
        if (!store.HasActive && !store.HasPending)
            throw new IOException("No connector identity is installed.");

        var endpoint = origin.WebSocketPath("/private-access/connectors");
        while (!cancellationToken.IsCancellationRequested)
        {
            if (store.HasPending)
                await TrySessionAsync(endpoint, store, pending: true, cancellationToken);
            if (!cancellationToken.IsCancellationRequested && store.HasActive)
                await TrySessionAsync(endpoint, store, pending: false, cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(300, 1200)),
                cancellationToken);
        }
    }

    private static async Task TrySessionAsync(
        Uri endpoint,
        ConnectorIdentityStore store,
        bool pending,
        CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            using var certificate = pending ? store.LoadPending() : store.LoadActive();
            using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
            handler.SslOptions.ClientCertificates = new System.Security.Cryptography.X509Certificates.X509CertificateCollection
            {
                certificate
            };
            using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(endpoint, invoker, connect.Token);
            await RunLeaseAsync(socket, pending ? store.PromotePending : null, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A timed-out connection or lease cannot authorize a session.
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or
            InvalidDataException or AuthenticationException or InvalidOperationException)
        {
            // A new outbound connection performs fresh certificate and target authorization.
        }
        finally
        {
            socket.Abort();
        }
    }

    private static async Task RunLeaseAsync(
        WebSocket socket,
        Action? firstAuthorization,
        CancellationToken cancellationToken)
    {
        var lease = new PrivateAccessLeaseClock(TimeProvider.System);
        while (true)
        {
            var challenge = lease.Challenge();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(lease.Remaining);
            await PrivateAccessFrames.SendAsync(
                socket,
                PrivateAccessFrameKind.LeaseChallenge,
                challenge,
                deadline.Token);
            var response = await PrivateAccessFrames.ReceiveAsync(socket, deadline.Token);
            if (response.Kind != PrivateAccessFrameKind.LeaseAccepted ||
                !lease.Accept(response.Payload.Span))
                throw new InvalidDataException("Connector authorization lease was rejected.");
            firstAuthorization?.Invoke();
            firstAuthorization = null;
            await Task.Delay(PrivateAccessLeaseClock.RenewalInterval, cancellationToken);
        }
    }
}
