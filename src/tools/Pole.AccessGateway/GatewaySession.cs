// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Pole.Features.PrivateAccess.Primitives;
using Pole.Features.PrivateAccess.Runtime;

namespace Pole.AccessGateway;

/// <summary>One outbound control channel and one outbound mTLS stream per TCP connection.</summary>
public static class GatewaySession
{
    public static async Task RunAsync(
        BrokerOrigin origin,
        string identityFile,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(identityFile))
            throw new ArgumentException("Gateway identity file is required.", nameof(identityFile));
        var nextDiagnostic = DateTimeOffset.MinValue;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var identity = X509CertificateLoader.LoadPkcs12FromFile(
                    identityFile,
                    "",
                    X509KeyStorageFlags.EphemeralKeySet);
                if (!identity.HasPrivateKey)
                    throw new IOException("Gateway identity lacks a private key.");
                await RunControlAsync(origin, identity, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A stale control lease or connection timeout requires fresh admission.
                ReportRetry(nameof(OperationCanceledException));
            }
            catch (Exception exception) when (exception is WebSocketException or IOException or InvalidDataException
                                                  or AuthenticationException or InvalidOperationException
                                                  or CryptographicException)
            {
                ReportRetry(exception.GetType().Name);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(300, 1200)), cancellationToken);
        }

        void ReportRetry(string failureType)
        {
            var now = DateTimeOffset.UtcNow;
            if (now < nextDiagnostic)
                return;
            Console.Error.WriteLine($"Access Gateway connection failed ({failureType}); retrying.");
            nextDiagnostic = now.AddSeconds(30);
        }
    }

    private static async Task RunControlAsync(
        BrokerOrigin origin,
        X509Certificate2 identity,
        CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using var handler = Handler(identity);
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(origin.WebSocketPath("/private-access/gateways"), invoker, lifetime.Token);
        using var writer = new PrivateAccessFrameWriter(socket);
        using var slots = new SemaphoreSlim(64, 64);
        var streams = new ConcurrentDictionary<Guid, Task>();
        var accepted = Channel.CreateBounded<bool>(1);
        var lease = new PrivateAccessLeaseClock(TimeProvider.System);
        var receiving = ReceiveAsync();
        try
        {
            while (true)
            {
                var challenge = lease.Challenge();
                lifetime.CancelAfter(lease.Remaining);
                await writer.SendAsync(PrivateAccessFrameKind.LeaseChallenge, challenge, lifetime.Token);
                await accepted.Reader.ReadAsync(lifetime.Token);
                await Task.Delay(PrivateAccessLeaseClock.RenewalInterval, lifetime.Token);
            }
        }
        finally
        {
            await lifetime.CancelAsync();
            socket.Abort();
            try
            {
                await receiving;
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException
                                                  or WebSocketException or InvalidDataException)
            {
            }

            await Task.WhenAll(streams.Values);
        }

        async Task ReceiveAsync()
        {
            try
            {
                while (true)
                {
                    var frame = await PrivateAccessFrames.ReceiveAsync(socket, lifetime.Token);
                    switch (frame.Kind)
                    {
                        case PrivateAccessFrameKind.LeaseAccepted:
                            if (!lease.Accept(frame.Payload.Span) || !accepted.Writer.TryWrite(true))
                                throw new InvalidDataException("Unexpected gateway lease response.");
                            break;
                        case PrivateAccessFrameKind.Data when lease.Remaining > TimeSpan.Zero:
                            var ticket = PrivateAccessGatewayProtocol.ReadOpenStream(frame);
                            if (!await slots.WaitAsync(0, lifetime.Token))
                                break;
                            var id = Guid.NewGuid();
                            var stream = RunStreamAsync(origin, identity, ticket, lifetime.Token);
                            streams[id] = stream;
                            _ = stream.ContinueWith(
                                completed =>
                                {
                                    if (completed.Exception is not null)
                                        // Observe unexpected faults; a stream is isolated from control.
                                        _ = completed.Exception;
                                    streams.TryRemove(id, out _);
                                    slots.Release();
                                },
                                CancellationToken.None,
                                TaskContinuationOptions.ExecuteSynchronously,
                                TaskScheduler.Default);
                            break;
                        default:
                            throw new InvalidDataException("Unexpected gateway control frame.");
                    }
                }
            }
            catch
            {
                await lifetime.CancelAsync();
                throw;
            }
        }
    }

    private static async Task RunStreamAsync(
        BrokerOrigin origin,
        X509Certificate2 identity,
        string ticket,
        CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {ticket}");
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using var handler = Handler(identity);
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(origin.WebSocketPath("/private-access/gateway-streams"), invoker, connect.Token);
            var frame = await PrivateAccessFrames.ReceiveAsync(socket, connect.Token);
            var destination = PrivateAccessGatewayProtocol.ReadDestination(frame);
            await new PrivateAccessTcpRelay(TimeProvider.System).RunAsync(
                socket,
                destination.ServiceDns,
                destination.ServicePort,
                cancellationToken);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or WebSocketException
                                              or InvalidDataException or AuthenticationException
                                              or InvalidOperationException)
        {
            socket.Abort();
        }
    }

    private static SocketsHttpHandler Handler(X509Certificate2 identity)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        handler.SslOptions.ClientCertificates = new X509CertificateCollection { identity };
        return handler;
    }
}
