// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Primitives;
using Pole.Features.PrivateAccess.Runtime;
using Pole.Workloads;
using Xunit;

namespace Pole.Features.PrivateAccess.Tests;

public sealed class PrivateAccessGatewayTransportTests
{
    [Fact]
    public async Task StreamsRouteAcrossReplicasAndRecoverAfterOwnerDisconnect()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var serverCertificate = Certificate("server", true);
        using var gatewayCertificate = Certificate("gateway", false);
        using var peerFile = new CertificateFile(serverCertificate);
        var gatewayPin = Pin(gatewayCertificate);
        var gatewayIdentity = new PrivateAccessGatewayIdentity("tenant-a", "target-a", "gateway-a", 1, 1);
        var ports = Enumerable.Range(0, 3)
            .Select(_ => (Gateway: FreePort(), Public: FreePort(), Internal: FreePort()))
            .ToArray();
        var origins = ports.Select(port => $"https://127.0.0.1:{port.Internal}").ToArray();
        var owners = new SharedOwners();
        var redeemed = new ConcurrentDictionary<string, byte>();
        var apps = new List<WebApplication>();
        try
        {
            for (var index = 0; index < 3; index++)
            {
                var replica = index;
                var port = ports[replica];
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
                builder.Logging.ClearProviders();
                var settings = new Dictionary<string, string?>
                {
                    ["PrivateAccess:Enabled"] = "true",
                    ["PrivateAccess:GatewayPort"] = port.Gateway.ToString(CultureInfo.InvariantCulture),
                    ["PrivateAccess:InternalPort"] = port.Internal.ToString(CultureInfo.InvariantCulture),
                    ["PrivateAccess:InstanceId"] = $"replica-{replica}",
                    ["PrivateAccess:InternalOrigin"] = origins[replica],
                    ["PrivateAccess:InternalCertificatePath"] = peerFile.Path,
                    ["PrivateAccess:InternalCertificateSha256Pin"] = Pin(serverCertificate),
                    ["PrivateAccess:GatewayImage"] = "test/gateway:1",
                    ["PrivateAccess:BrokerUri"] = $"https://127.0.0.1:{port.Gateway}",
                    ["PrivateAccess:BrokerAddresses:0"] = "192.0.2.8",
                    ["Kestrel:Endpoints:PrivateAccessGateway:Url"] = $"https://127.0.0.1:{port.Gateway}",
                    ["Kestrel:Endpoints:PrivateAccessInternal:Url"] = origins[replica],
                    ["Kestrel:Endpoints:Public:Url"] = $"https://127.0.0.1:{port.Public}",
                };
                for (var peer = 0; peer < origins.Length; peer++)
                    settings[$"PrivateAccess:InternalPeerOrigins:{peer}"] = origins[peer];
                builder.Configuration.AddInMemoryCollection(settings);
                builder.WebHost.ConfigureKestrel(options =>
                    options.ConfigureHttpsDefaults(https => https.ServerCertificate = serverCertificate));
                builder.ConfigurePrivateAccessListener();
                builder.Services.AddPrivateAccessRuntime(builder.Configuration);
                builder.Services.AddSingleton<ClaimPrivateAccessBrokerOwner>(owners.ClaimAsync);
                builder.Services.AddSingleton<RenewPrivateAccessBrokerOwner>(owners.RenewAsync);
                builder.Services.AddSingleton<ReleasePrivateAccessBrokerOwner>(owners.ReleaseAsync);
                builder.Services.AddSingleton<LoadPrivateAccessBrokerOwner>(owners.LoadAsync);
                builder.Services.AddSingleton<ResolvePrivateAccessGatewayIdentity>((pin, _) =>
                    Task.FromResult(pin == gatewayPin ? gatewayIdentity : null));
                builder.Services.AddSingleton<RecordPrivateAccessGatewayContact>((
                    _, _,
                    _) => Task.FromResult(true));
                builder.Services.AddSingleton<LocatePrivateAccessTicket>((ticket, _) => Task.FromResult(
                    ticket is "multi-1" or "multi-2" ? new PrivateAccessTicketRoute("tenant-a", "target-a") : null));
                builder.Services.AddSingleton<RedeemPrivateAccessSession>((ticket, _) =>
                    Task.FromResult<RedeemPrivateAccessSessionOutcome>(
                        ticket is "multi-1" or "multi-2" && redeemed.TryAdd(ticket, 0)
                            ? Lease(ticket)
                            : new PrivateAccessTicketUsed()));
                builder.Services.AddSingleton<RenewPrivateAccessSession>((sessionId, _) =>
                    Task.FromResult<RenewPrivateAccessSessionOutcome>(Lease(sessionId)));
                builder.Services.AddSingleton<MarkPrivateAccessSessionStarted>((_, _) =>
                    Task.FromResult<MarkPrivateAccessSessionStartedOutcome>(new PrivateAccessSessionStarted()));
                builder.Services.AddSingleton<ClosePrivateAccessSession>((
                    _, _,
                    _) => Task.FromResult<ClosePrivateAccessSessionOutcome>(new PrivateAccessSessionClosed()));
                builder.Services.AddSingleton<LoadExpiredPrivateAccessSessionCleanupPage>((
                    _, _,
                    _) => Task.FromResult<IReadOnlyList<PrivateAccessSessionCleanupSnapshot>>([]));
                builder.Services.AddSingleton<MarkPrivateAccessSessionCleaned>((_, _) =>
                    Task.FromResult<MarkPrivateAccessSessionCleanedOutcome>(new PrivateAccessSessionCleaned()));
                var app = builder.Build();
                app.UsePrivateAccessRuntime();
                app.MapPrivateAccessGatewayControlEndpoint();
                app.MapPrivateAccessGatewayStreamEndpoint();
                app.MapPrivateAccessStreamEndpoint();
                await app.StartAsync(timeout.Token);
                apps.Add(app);
            }

            await ExerciseAsync(0, 1, 2, "multi-1");
            await SpinUntilAsync(() => owners.Current is null, timeout.Token);
            await ExerciseAsync(2, 0, 1, "multi-2");
        }
        finally
        {
            foreach (var app in apps)
            {
                await app.StopAsync(CancellationToken.None);
                await app.DisposeAsync();
            }
        }

        async Task ExerciseAsync(
            int controlReplica,
            int userReplica,
            int streamReplica,
            string userTicket)
        {
            using var control = Socket(serverCertificate, gatewayCertificate);
            await control.ConnectAsync(
                new Uri($"wss://127.0.0.1:{ports[controlReplica].Gateway}/private-access/gateways"),
                timeout.Token);
            await ChallengeAsync(control, timeout.Token);
            Assert.Equal($"replica-{controlReplica}", owners.Current?.InstanceId);

            using var user = Socket(serverCertificate, bearer: userTicket);
            var userConnect = user.ConnectAsync(
                new Uri($"wss://127.0.0.1:{ports[userReplica].Public}/private-access/streams"),
                timeout.Token);
            var open = await PrivateAccessFrames.ReceiveAsync(control, timeout.Token);
            var gatewayTicket = PrivateAccessGatewayProtocol.ReadOpenStream(open);
            if (userTicket == "multi-2")
            {
                await Task.Delay(TimeSpan.FromSeconds(6), timeout.Token);
                await ChallengeAsync(control, timeout.Token);
                await Task.Delay(TimeSpan.FromSeconds(6), timeout.Token);
                await ChallengeAsync(control, timeout.Token);
            }

            using var stream = Socket(serverCertificate, gatewayCertificate, gatewayTicket);
            await stream.ConnectAsync(
                new Uri($"wss://127.0.0.1:{ports[streamReplica].Gateway}/private-access/gateway-streams"),
                timeout.Token);
            var destination = PrivateAccessGatewayProtocol.ReadDestination(
                await PrivateAccessFrames.ReceiveAsync(stream, timeout.Token));
            Assert.Equal("database.runtime-a.svc", destination.ServiceDns);
            await userConnect;
            await PrivateAccessFrames.SendAsync(
                stream,
                PrivateAccessFrameKind.Ready,
                ReadOnlyMemory<byte>.Empty,
                timeout.Token);
            await PrivateAccessFrames.SendAsync(
                user,
                PrivateAccessFrameKind.Data,
                Encoding.UTF8.GetBytes(userTicket),
                timeout.Token);
            var inbound = await PrivateAccessFrames.ReceiveAsync(stream, timeout.Token);
            Assert.Equal(userTicket, Encoding.UTF8.GetString(inbound.Payload.Span));
            await PrivateAccessFrames.SendAsync(
                stream,
                PrivateAccessFrameKind.Data,
                Encoding.UTF8.GetBytes("reply"),
                timeout.Token);
            var outbound = await PrivateAccessFrames.ReceiveAsync(user, timeout.Token);
            Assert.Equal("reply", Encoding.UTF8.GetString(outbound.Payload.Span));
            using var replay = Socket(serverCertificate, gatewayCertificate, gatewayTicket);
            await Assert.ThrowsAsync<WebSocketException>(() => replay.ConnectAsync(
                new Uri($"wss://127.0.0.1:{ports[streamReplica].Gateway}/private-access/gateway-streams"),
                timeout.Token));
            if (userTicket == "multi-1")
            {
                owners.Expire();
                using var takeover = Socket(serverCertificate, gatewayCertificate);
                await takeover.ConnectAsync(
                    new Uri($"wss://127.0.0.1:{ports[streamReplica].Gateway}/private-access/gateways"),
                    timeout.Token);
                await ChallengeAsync(takeover, timeout.Token);
                Assert.Equal($"replica-{streamReplica}", owners.Current?.InstanceId);
                await SpinUntilAsync(
                    () => apps[controlReplica]
                        .Services
                        .GetRequiredService<PrivateAccessGatewayRegistry>()
                        .Find("tenant-a", "target-a") is null,
                    timeout.Token);
                takeover.Abort();
            }

            user.Abort();
            stream.Abort();
            control.Abort();
        }
    }

    [Fact]
    public async Task TenantBoundGatewayStreamsRelayOnlyAfterFreshLease()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var serverCertificate = Certificate("server", true);
        using var gatewayCertificate = Certificate("gateway-a", false);
        using var otherCertificate = Certificate("gateway-b", false);
        using var otherTargetCertificate = Certificate("gateway-c", false);
        using var peerFile = new CertificateFile(serverCertificate);
        var gatewayPin = Pin(gatewayCertificate);
        var otherPin = Pin(otherCertificate);
        var otherTargetPin = Pin(otherTargetCertificate);
        var gatewayIdentity = new PrivateAccessGatewayIdentity("tenant-a", "target-a", "gateway-a", 1, 1);
        var otherIdentity = new PrivateAccessGatewayIdentity("tenant-b", "target-a", "gateway-b", 1, 1);
        var otherTargetIdentity = new PrivateAccessGatewayIdentity("tenant-a", "target-b", "gateway-c", 1, 1);
        var connectorPort = FreePort();
        var publicPort = FreePort();
        var internalPort = FreePort();
        var internalOrigin = $"https://127.0.0.1:{internalPort}";
        var owners = new InMemoryOwners("test-instance", internalOrigin);
        var revoked = false;
        var started = 0;
        var renewed = 0;
        var closeCount = 0;
        var redeemed = new ConcurrentDictionary<string, byte>();
        var reachedRenewal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRenewal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource<PrivateAccessSessionCloseReason>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["PrivateAccess:Enabled"] = "true",
                ["PrivateAccess:GatewayPort"] = connectorPort.ToString(CultureInfo.InvariantCulture),
                ["PrivateAccess:GatewayImage"] = "test/gateway:1",
                ["PrivateAccess:BrokerUri"] = $"https://127.0.0.1:{connectorPort}",
                ["PrivateAccess:BrokerAddresses:0"] = "192.0.2.8",
                ["PrivateAccess:InternalPort"] = internalPort.ToString(CultureInfo.InvariantCulture),
                ["PrivateAccess:InstanceId"] = "test-instance",
                ["PrivateAccess:InternalOrigin"] = internalOrigin,
                ["PrivateAccess:InternalPeerOrigins:0"] = internalOrigin,
                ["PrivateAccess:InternalCertificatePath"] = peerFile.Path,
                ["PrivateAccess:InternalCertificateSha256Pin"] = Pin(serverCertificate),
                ["Kestrel:Endpoints:PrivateAccessGateway:Url"] = $"https://127.0.0.1:{connectorPort}",
                ["Kestrel:Endpoints:PrivateAccessInternal:Url"] = internalOrigin,
                ["Kestrel:Endpoints:Public:Url"] = $"https://127.0.0.1:{publicPort}",
            });
        builder.WebHost.ConfigureKestrel(options => options.ConfigureHttpsDefaults(https =>
            https.ServerCertificate = serverCertificate));
        builder.ConfigurePrivateAccessListener();
        builder.Services.AddPrivateAccessRuntime(builder.Configuration);
        builder.Services.AddSingleton<ClaimPrivateAccessBrokerOwner>(owners.ClaimAsync);
        builder.Services.AddSingleton<RenewPrivateAccessBrokerOwner>(owners.RenewAsync);
        builder.Services.AddSingleton<ReleasePrivateAccessBrokerOwner>(owners.ReleaseAsync);
        builder.Services.AddSingleton<LoadPrivateAccessBrokerOwner>(owners.LoadAsync);
        builder.Services.AddSingleton<LocatePrivateAccessTicket>((ticket, _) => Task.FromResult(
            ticket is "user-ticket" or "user-ticket-2" ? new PrivateAccessTicketRoute("tenant-a", "target-a") : null));
        builder.Services.AddSingleton<ResolvePrivateAccessGatewayIdentity>((pin, _) => Task.FromResult(
            pin == gatewayPin && !revoked ? gatewayIdentity :
            pin == otherPin ? otherIdentity :
            pin == otherTargetPin ? otherTargetIdentity : null));
        builder.Services.AddSingleton<RecordPrivateAccessGatewayContact>((
            _, _,
            _) => Task.FromResult(true));
        builder.Services.AddSingleton<RedeemPrivateAccessSession>((ticket, _) =>
            Task.FromResult<RedeemPrivateAccessSessionOutcome>(
                ticket is "user-ticket" or "user-ticket-2" && redeemed.TryAdd(ticket, 0)
                    ? Lease(ticket == "user-ticket" ? "session-a" : "session-b")
                    : new PrivateAccessTicketInvalid()));
        builder.Services.AddSingleton<RenewPrivateAccessSession>(async (sessionId, ct) =>
        {
            Interlocked.Increment(ref renewed);
            reachedRenewal.TrySetResult();
            await allowRenewal.Task.WaitAsync(ct);
            return revoked ? new PrivateAccessSessionUnavailable() : Lease(sessionId);
        });
        builder.Services.AddSingleton<MarkPrivateAccessSessionStarted>((_, _) =>
        {
            Interlocked.Increment(ref started);
            return Task.FromResult<MarkPrivateAccessSessionStartedOutcome>(new PrivateAccessSessionStarted());
        });
        builder.Services.AddSingleton<ClosePrivateAccessSession>((
            _, reason,
            _) =>
        {
            Interlocked.Increment(ref closeCount);
            closed.TrySetResult(reason);
            return Task.FromResult<ClosePrivateAccessSessionOutcome>(new PrivateAccessSessionClosed());
        });
        builder.Services.AddSingleton<LoadExpiredPrivateAccessSessionCleanupPage>((
            _, _,
            _) => Task.FromResult<IReadOnlyList<PrivateAccessSessionCleanupSnapshot>>([]));
        builder.Services.AddSingleton<MarkPrivateAccessSessionCleaned>((_, _) =>
            Task.FromResult<MarkPrivateAccessSessionCleanedOutcome>(new PrivateAccessSessionCleaned()));
        await using var app = builder.Build();
        app.UsePrivateAccessRuntime();
        app.MapPrivateAccessGatewayControlEndpoint();
        app.MapPrivateAccessGatewayStreamEndpoint();
        app.MapPrivateAccessStreamEndpoint();
        await app.StartAsync(timeout.Token);

        var controlUri = new Uri($"wss://127.0.0.1:{connectorPort}/private-access/gateways");
        var gatewayStreamUri = new Uri($"wss://127.0.0.1:{connectorPort}/private-access/gateway-streams");
        var userStreamUri = new Uri($"wss://127.0.0.1:{publicPort}/private-access/streams");
        using var control = Socket(serverCertificate, gatewayCertificate);
        await control.ConnectAsync(controlUri, timeout.Token);
        await ChallengeAsync(control, timeout.Token);
        using var otherTenantControl = Socket(serverCertificate, otherCertificate);
        await otherTenantControl.ConnectAsync(controlUri, timeout.Token);
        await ChallengeAsync(otherTenantControl, timeout.Token);
        using var otherTargetControl = Socket(serverCertificate, otherTargetCertificate);
        await otherTargetControl.ConnectAsync(controlUri, timeout.Token);
        await ChallengeAsync(otherTargetControl, timeout.Token);
        Assert.NotNull(app.Services.GetRequiredService<PrivateAccessGatewayRegistry>().Find("tenant-a", "target-a"));
        Assert.NotNull(app.Services.GetRequiredService<PrivateAccessGatewayRegistry>().Find("tenant-b", "target-a"));

        using var user = Socket(serverCertificate, bearer: "user-ticket");
        var connectingUser = user.ConnectAsync(userStreamUri, timeout.Token);
        var open = await PrivateAccessFrames.ReceiveAsync(control, timeout.Token);
        var ticket = PrivateAccessGatewayProtocol.ReadOpenStream(open);
        using var wrongTenant = Socket(serverCertificate, otherCertificate, ticket);
        await Assert.ThrowsAsync<WebSocketException>(() => wrongTenant.ConnectAsync(gatewayStreamUri, timeout.Token));
        using var wrongTarget = Socket(serverCertificate, otherTargetCertificate, ticket);
        await Assert.ThrowsAsync<WebSocketException>(() => wrongTarget.ConnectAsync(gatewayStreamUri, timeout.Token));
        using var gatewayStream = Socket(serverCertificate, gatewayCertificate, ticket);
        await gatewayStream.ConnectAsync(gatewayStreamUri, timeout.Token);
        using var replay = Socket(serverCertificate, gatewayCertificate, ticket);
        await Assert.ThrowsAsync<WebSocketException>(() => replay.ConnectAsync(gatewayStreamUri, timeout.Token));
        var destination = PrivateAccessGatewayProtocol.ReadDestination(
            await PrivateAccessFrames.ReceiveAsync(gatewayStream, timeout.Token));
        Assert.Equal("database.runtime-a.svc", destination.ServiceDns);
        Assert.Equal(5432, destination.ServicePort);
        await connectingUser;

        using var backend = new TcpListener(IPAddress.Loopback, 0);
        backend.Start();
        var gatewayRun = new PrivateAccessTcpRelay(TimeProvider.System).RunAsync(
            gatewayStream,
            "127.0.0.1",
            ((IPEndPoint)backend.LocalEndpoint).Port,
            timeout.Token);
        await reachedRenewal.Task.WaitAsync(timeout.Token);
        Assert.False(backend.Pending());
        await PrivateAccessFrames.SendAsync(
            user,
            PrivateAccessFrameKind.Data,
            Encoding.UTF8.GetBytes("request"),
            timeout.Token);
        Assert.False(backend.Pending());
        allowRenewal.TrySetResult();
        using var backendConnection = await backend.AcceptTcpClientAsync(timeout.Token);
        var request = new byte[7];
        await backendConnection.GetStream().ReadExactlyAsync(request, timeout.Token);
        Assert.Equal("request", Encoding.UTF8.GetString(request));
        Assert.Equal(1, started);
        Assert.True(renewed > 0);
        await backendConnection.GetStream().WriteAsync(Encoding.UTF8.GetBytes("response"), timeout.Token);
        var response = await PrivateAccessFrames.ReceiveAsync(user, timeout.Token);
        Assert.Equal(PrivateAccessFrameKind.Data, response.Kind);
        Assert.Equal("response", Encoding.UTF8.GetString(response.Payload.Span));

        using var secondUser = Socket(serverCertificate, bearer: "user-ticket-2");
        var connectingSecondUser = secondUser.ConnectAsync(userStreamUri, timeout.Token);
        var secondTicket = PrivateAccessGatewayProtocol.ReadOpenStream(
            await PrivateAccessFrames.ReceiveAsync(control, timeout.Token));
        Assert.NotEqual(ticket, secondTicket);
        using var secondGatewayStream = Socket(serverCertificate, gatewayCertificate, secondTicket);
        await secondGatewayStream.ConnectAsync(gatewayStreamUri, timeout.Token);
        _ = PrivateAccessGatewayProtocol.ReadDestination(
            await PrivateAccessFrames.ReceiveAsync(secondGatewayStream, timeout.Token));
        await connectingSecondUser;
        using var secondBackend = new TcpListener(IPAddress.Loopback, 0);
        secondBackend.Start();
        var secondGatewayRun = new PrivateAccessTcpRelay(TimeProvider.System).RunAsync(
            secondGatewayStream,
            "127.0.0.1",
            ((IPEndPoint)secondBackend.LocalEndpoint).Port,
            timeout.Token);
        await PrivateAccessFrames.SendAsync(
            secondUser,
            PrivateAccessFrameKind.Data,
            Encoding.UTF8.GetBytes("second"),
            timeout.Token);
        using var secondConnection = await secondBackend.AcceptTcpClientAsync(timeout.Token);
        var secondRequest = new byte[6];
        await secondConnection.GetStream().ReadExactlyAsync(secondRequest, timeout.Token);
        Assert.Equal("second", Encoding.UTF8.GetString(secondRequest));
        Assert.Equal(2, started);

        revoked = true;
        await PrivateAccessFrames.SendAsync(
            control,
            PrivateAccessFrameKind.LeaseChallenge,
            RandomNumberGenerator.GetBytes(32),
            timeout.Token);
        await closed.Task.WaitAsync(timeout.Token);
        await SpinUntilAsync(() => Volatile.Read(ref closeCount) == 2, timeout.Token);
        Assert.Null(app.Services.GetRequiredService<PrivateAccessGatewayRegistry>().Find("tenant-a", "target-a"));
        var terminal = await Record.ExceptionAsync(async () => await gatewayRun.WaitAsync(timeout.Token));
        Assert.True(
            terminal is null or OperationCanceledException or IOException or WebSocketException,
            terminal?.GetType().Name);
        var secondTerminal = await Record.ExceptionAsync(async () => await secondGatewayRun.WaitAsync(timeout.Token));
        Assert.True(
            secondTerminal is null or OperationCanceledException or IOException or WebSocketException,
            secondTerminal?.GetType().Name);

        revoked = false;
        using var recovered = Socket(serverCertificate, gatewayCertificate);
        await recovered.ConnectAsync(controlUri, timeout.Token);
        await ChallengeAsync(recovered, timeout.Token);
        Assert.NotNull(app.Services.GetRequiredService<PrivateAccessGatewayRegistry>().Find("tenant-a", "target-a"));
        recovered.Abort();
        await SpinUntilAsync(
            () => app.Services.GetRequiredService<PrivateAccessGatewayRegistry>().Find("tenant-a", "target-a") is null,
            timeout.Token);
        await app.StopAsync(timeout.Token);
    }

    private static PrivateAccessLeaseSnapshot Lease(string sessionId) => new(
        "tenant-a",
        "user-a",
        "grant-a",
        sessionId,
        "postgresql",
        "resource-a",
        "deployment-a",
        "endpoint-a",
        "target-a",
        "cell-a",
        "scope-a",
        "runtime-a",
        "database",
        NetworkPort.Create(5432),
        NetworkPort.Create(5432),
        1,
        DateTime.UtcNow.AddMinutes(1),
        DateTime.UtcNow.AddMinutes(2));

    private static async Task ChallengeAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        await PrivateAccessFrames.SendAsync(socket, PrivateAccessFrameKind.LeaseChallenge, nonce, cancellationToken);
        var accepted = await PrivateAccessFrames.ReceiveAsync(socket, cancellationToken);
        Assert.Equal(PrivateAccessFrameKind.LeaseAccepted, accepted.Kind);
        Assert.Equal(nonce, accepted.Payload.ToArray());
    }

    private static string Pin(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();

    private static async Task SpinUntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        while (!predicate())
            await Task.Delay(20, cancellationToken);
    }

    private static ClientWebSocket Socket(
        X509Certificate2 server,
        X509Certificate2? client = null,
        string? bearer = null)
    {
        var socket = new ClientWebSocket();
        if (client is not null)
            socket.Options.ClientCertificates.Add(client);
        if (bearer is not null)
            socket.Options.SetRequestHeader("Authorization", $"Bearer {bearer}");
        socket.Options.RemoteCertificateValidationCallback = (
            _, certificate,
            _, _) => certificate is not null && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(certificate.GetRawCertData()),
            SHA256.HashData(server.RawData));
        return socket;
    }

    private static X509Certificate2 Certificate(string name, bool server)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") },
                false));
        if (server)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
        }

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class CertificateFile : IDisposable
    {
        public CertificateFile(X509Certificate2 certificate)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pole-peer-{Guid.NewGuid():N}.pfx");
            File.WriteAllBytes(Path, certificate.Export(X509ContentType.Pfx));
        }

        public string Path { get; }
        public void Dispose() => File.Delete(Path);
    }

    private sealed class InMemoryOwners(
        string instanceId,
        string origin
    )
    {
        private readonly ConcurrentDictionary<(string TenantId, string TargetId), PrivateAccessBrokerOwnerLease> owners
            = new();

        private long epoch;

        public Task<ClaimPrivateAccessBrokerOwnerOutcome> ClaimAsync(
            ClaimPrivateAccessBrokerOwnerRequest request,
            CancellationToken cancellationToken)
        {
            lock (owners)
            {
                if (request.InstanceId != instanceId || request.InternalOrigin != origin)
                    return Task.FromResult<ClaimPrivateAccessBrokerOwnerOutcome>(new PrivateAccessBrokerOwnerBusy());
                var lease = new PrivateAccessBrokerOwnerLease(
                    request.Gateway,
                    instanceId,
                    origin,
                    Interlocked.Increment(ref epoch),
                    DateTime.UtcNow.AddSeconds(10));
                owners[(request.Gateway.TenantId, request.Gateway.DeploymentTargetId)] = lease;
                return Task.FromResult<ClaimPrivateAccessBrokerOwnerOutcome>(lease);
            }
        }

        public Task<bool> RenewAsync(PrivateAccessBrokerOwnerLease lease, CancellationToken cancellationToken)
        {
            lock (owners)
            {
                var key = (lease.Gateway.TenantId, lease.Gateway.DeploymentTargetId);
                if (!owners.TryGetValue(key, out var current) || current.Epoch != lease.Epoch)
                    return Task.FromResult(false);
                owners[key] = current with { ExpiresAtUtc = DateTime.UtcNow.AddSeconds(10) };
                return Task.FromResult(true);
            }
        }

        public Task ReleaseAsync(PrivateAccessBrokerOwnerLease lease, CancellationToken cancellationToken)
        {
            lock (owners)
            {
                var key = (lease.Gateway.TenantId, lease.Gateway.DeploymentTargetId);
                if (owners.TryGetValue(key, out var current) && current.Epoch == lease.Epoch)
                    owners.TryRemove(key, out _);
                return Task.CompletedTask;
            }
        }

        public Task<PrivateAccessBrokerOwnerLease?> LoadAsync(
            string tenantId,
            string targetId,
            CancellationToken cancellationToken)
        {
            owners.TryGetValue((tenantId, targetId), out var owner);
            return Task.FromResult(owner is { } current && current.ExpiresAtUtc > DateTime.UtcNow ? current : null);
        }
    }

    private sealed class SharedOwners
    {
        private long epoch;
        private PrivateAccessBrokerOwnerLease? owner;

        public PrivateAccessBrokerOwnerLease? Current
        {
            get
            {
                lock (this)
                {
                    return owner;
                }
            }
        }

        public void Expire()
        {
            lock (this)
            {
                if (owner is not null)
                    owner = owner with { ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1) };
            }
        }

        public Task<ClaimPrivateAccessBrokerOwnerOutcome> ClaimAsync(
            ClaimPrivateAccessBrokerOwnerRequest request,
            CancellationToken cancellationToken)
        {
            lock (this)
            {
                if (owner is { } current && current.ExpiresAtUtc > DateTime.UtcNow &&
                    current.InstanceId != request.InstanceId)
                    return Task.FromResult<ClaimPrivateAccessBrokerOwnerOutcome>(new PrivateAccessBrokerOwnerBusy());
                owner = new PrivateAccessBrokerOwnerLease(
                    request.Gateway,
                    request.InstanceId,
                    request.InternalOrigin,
                    ++epoch,
                    DateTime.UtcNow.AddSeconds(10));
                return Task.FromResult<ClaimPrivateAccessBrokerOwnerOutcome>(owner);
            }
        }

        public Task<bool> RenewAsync(PrivateAccessBrokerOwnerLease lease, CancellationToken cancellationToken)
        {
            lock (this)
            {
                if (owner?.Epoch != lease.Epoch || owner.ExpiresAtUtc <= DateTime.UtcNow)
                    return Task.FromResult(false);
                owner = owner with { ExpiresAtUtc = DateTime.UtcNow.AddSeconds(10) };
                return Task.FromResult(true);
            }
        }

        public Task ReleaseAsync(PrivateAccessBrokerOwnerLease lease, CancellationToken cancellationToken)
        {
            lock (this)
            {
                if (owner?.Epoch == lease.Epoch)
                    owner = null;
                return Task.CompletedTask;
            }
        }

        public Task<PrivateAccessBrokerOwnerLease?> LoadAsync(
            string tenantId,
            string targetId,
            CancellationToken cancellationToken)
        {
            lock (this)
            {
                return Task.FromResult(
                    owner is { } current && current.Gateway.TenantId == tenantId &&
                    current.Gateway.DeploymentTargetId == targetId && current.ExpiresAtUtc > DateTime.UtcNow
                        ? current
                        : null);
            }
        }
    }
}
