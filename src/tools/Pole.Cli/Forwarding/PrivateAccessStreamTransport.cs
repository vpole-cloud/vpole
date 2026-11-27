// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net.WebSockets;
using Pole.Cli.Api;
using Pole.Cli.Authentication;
using Pole.Features.PrivateAccess.Primitives;

namespace Pole.Cli.Forwarding;

public interface IPrivateAccessStreamTransport
{
    Task<IPrivateAccessConnection> OpenAsync(
        Uri server,
        string ticket,
        CancellationToken cancellationToken);
}

public interface IPrivateAccessConnection : IAsyncDisposable
{
    ValueTask<PrivateAccessReadResult> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);

    ValueTask CompleteWritesAsync(CancellationToken cancellationToken);

    // Must abort the underlying transport and unblock any pending read or write.
    void Abort();
}

public readonly record struct PrivateAccessReadResult(
    int BytesRead,
    bool RemoteClosed
);

public interface IPrivateAccessConnectionFactory
{
    Task<IPrivateAccessConnection> OpenAsync(
        string tenantId,
        string grantId,
        CancellationToken cancellationToken);
}

public sealed class AuthorizedPrivateAccessConnectionFactory(
    Uri server,
    IPrivateAccessSessionSource sessionSource,
    IPrivateAccessStreamTransport streamTransport
) : IPrivateAccessConnectionFactory
{
    public async Task<IPrivateAccessConnection> OpenAsync(
        string tenantId,
        string grantId,
        CancellationToken cancellationToken)
    {
        var session = await sessionSource.CreateSessionAsync(tenantId, grantId, cancellationToken);
        return await streamTransport.OpenAsync(server, session.Ticket, cancellationToken);
    }
}

public sealed class WebSocketPrivateAccessStreamTransport : IPrivateAccessStreamTransport
{
    public async Task<IPrivateAccessConnection> OpenAsync(
        Uri server,
        string ticket,
        CancellationToken cancellationToken)
    {
        if (!PrivateAccessApiClient.IsHttpToken(ticket))
            throw new InvalidOperationException("The one-use Private Access ticket is invalid.");

        var endpoint = GetEndpoint(server);
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + ticket);
        try
        {
            using var handler = CreateNoRedirectHandler();
            using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
            await socket.ConnectAsync(endpoint, invoker, cancellationToken);
            return new WebSocketPrivateAccessConnection(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal static SocketsHttpHandler CreateNoRedirectHandler() => new() { AllowAutoRedirect = false };

    internal static Uri GetEndpoint(Uri server)
    {
        CliTokenStore.ValidateServer(server);
        if (server.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Private Access forwarding requires an HTTPS Pole server.");
        return new UriBuilder(server)
        {
            Scheme = "wss",
            Path = "/private-access/streams",
            Query = "",
            Fragment = "",
        }.Uri;
    }
}

internal sealed class WebSocketPrivateAccessConnection(
    WebSocket socket
) : IPrivateAccessConnection
{
    private readonly PrivateAccessFrameWriter writer = new(socket);
    private int currentOffset;
    private byte[]? currentPayload;
    private bool readClosed;
    private bool remoteClosed;
    private bool writeClosed;

    public async ValueTask<PrivateAccessReadResult> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (buffer.IsEmpty)
            return new PrivateAccessReadResult(0, remoteClosed);
        while (!readClosed)
        {
            if (currentPayload is { } payload && currentOffset < payload.Length)
            {
                var count = Math.Min(buffer.Length, payload.Length - currentOffset);
                payload.AsMemory(currentOffset, count).CopyTo(buffer);
                currentOffset += count;
                if (currentOffset == payload.Length)
                {
                    currentPayload = null;
                    currentOffset = 0;
                }

                return new PrivateAccessReadResult(count, false);
            }

            var frame = await PrivateAccessFrames.ReceiveAsync(socket, cancellationToken);
            switch (frame.Kind)
            {
                case PrivateAccessFrameKind.Data:
                    currentPayload = frame.Payload.ToArray();
                    break;
                case PrivateAccessFrameKind.HalfClose:
                    readClosed = true;
                    break;
                case PrivateAccessFrameKind.Closed:
                    readClosed = true;
                    remoteClosed = true;
                    break;
                case PrivateAccessFrameKind.LeaseChallenge:
                case PrivateAccessFrameKind.LeaseAccepted:
                case PrivateAccessFrameKind.Ready:
                default:
                    throw new InvalidDataException("The user Private Access stream sent an unsupported control frame.");
            }
        }

        return new PrivateAccessReadResult(0, remoteClosed);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        if (writeClosed)
            throw new InvalidOperationException("Private Access writes are already closed.");
        if (buffer.IsEmpty || buffer.Length > PrivateAccessFrames.MaximumPayloadLength)
            throw new InvalidDataException("Invalid Private Access data frame size.");
        await writer.SendAsync(PrivateAccessFrameKind.Data, buffer, cancellationToken);
    }

    public async ValueTask CompleteWritesAsync(CancellationToken cancellationToken)
    {
        if (writeClosed)
            return;
        writeClosed = true;
        await writer.SendAsync(PrivateAccessFrameKind.HalfClose, ReadOnlyMemory<byte>.Empty, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (socket.State == WebSocketState.Open)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            try
            {
                await writer.SendAsync(PrivateAccessFrameKind.Closed, ReadOnlyMemory<byte>.Empty, timeout.Token);
            }
            catch (Exception error) when (error is WebSocketException or OperationCanceledException)
            {
                socket.Abort();
            }
        }

        writer.Dispose();
        socket.Dispose();
    }

    public void Abort() => socket.Abort();
}
