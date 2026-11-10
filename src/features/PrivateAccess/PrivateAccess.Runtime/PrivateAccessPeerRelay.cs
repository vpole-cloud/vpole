using System.Buffers;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Pole.Features.PrivateAccess.Contracts;

namespace Pole.Features.PrivateAccess.Runtime;

public sealed class PrivateAccessPeerRelay(
    IOptions<PrivateAccessBrokerOptions> configured,
    PrivateAccessInternalIdentity identity,
    PrivateAccessDrainState drain
)
{
    public const string FingerprintHeader = "X-Pole-Gateway-Certificate-Sha256";

    public async Task<bool> IsAuthenticatedAsync(HttpContext context)
    {
        if (context.Connection.LocalPort != configured.Value.InternalPort || !context.Request.IsHttps ||
            !context.WebSockets.IsWebSocketRequest)
            return false;
        var certificate = await context.Connection.GetClientCertificateAsync(context.RequestAborted);
        return identity.Matches(certificate);
    }

    public async Task ProxyAsync(
        HttpContext context,
        PrivateAccessBrokerOwnerLease owner,
        string path,
        string? ticket = null,
        string? fingerprint = null)
    {
        if (!configured.Value.InternalPeerOrigins.Contains(owner.InternalOrigin, StringComparer.Ordinal))
        {
            context.Response.StatusCode = 503;
            return;
        }

        using var upstream = new ClientWebSocket();
        if (ticket is not null)
            upstream.Options.SetRequestHeader("Authorization", $"Bearer {ticket}");
        if (fingerprint is not null)
            upstream.Options.SetRequestHeader(FingerprintHeader, fingerprint);
        using var handler = new SocketsHttpHandler();
        handler.SslOptions.ClientCertificates = [identity.Certificate];
        handler.SslOptions.RemoteCertificateValidationCallback = (
            _, certificate,
            _, _) => identity.Matches(certificate);
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        var uri = new UriBuilder(new Uri(new Uri(owner.InternalOrigin), path.TrimStart('/')))
        {
            Scheme = Uri.UriSchemeWss,
        }.Uri;

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, drain.Deadline);
        lifetime.CancelAfter(
            path == "/_private-access/relay/streams" ? TimeSpan.FromSeconds(35) : TimeSpan.FromSeconds(10));
        try
        {
            await upstream.ConnectAsync(uri, invoker, lifetime.Token);
            using var downstream = await context.WebSockets.AcceptWebSocketAsync();
            lifetime.CancelAfter(Timeout.InfiniteTimeSpan);
            var forward = PumpAsync(downstream, upstream, lifetime.Token);
            var backward = PumpAsync(upstream, downstream, lifetime.Token);
            await Task.WhenAny(forward, backward);
            await lifetime.CancelAsync();
            downstream.Abort();
            upstream.Abort();
            await ObserveAsync(forward);
            await ObserveAsync(backward);
        }
        catch (Exception exception) when (exception is WebSocketException or HttpRequestException or IOException
                                              or OperationCanceledException)
        {
            if (context.Response.HasStarted)
                context.Abort();
            else
                context.Response.StatusCode = 503;
        }
    }

    private static async Task PumpAsync(
        WebSocket source,
        WebSocket destination,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var result = await source.ReceiveAsync(buffer.AsMemory(), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await destination.CloseOutputAsync(
                        source.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                        source.CloseStatusDescription,
                        cancellationToken);
                    return;
                }

                if (result.MessageType != WebSocketMessageType.Binary)
                    throw new InvalidDataException("Private Access relay requires binary frames.");
                await destination.SendAsync(
                    buffer.AsMemory(0, result.Count),
                    WebSocketMessageType.Binary,
                    result.EndOfMessage,
                    cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or OperationCanceledException
                                              or InvalidDataException)
        {
        }
    }
}
