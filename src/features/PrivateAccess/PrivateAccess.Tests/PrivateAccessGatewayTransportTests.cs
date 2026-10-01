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
    public async Task TenantBoundGatewayStreamsRelayOnlyAfterFreshLease()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var serverCertificate = Certificate("server", true);
        using var gatewayCertificate = Certificate("gateway-a", false);
        using var otherCertificate = Certificate("gateway-b", false);
        using var otherTargetCertificate = Certificate("gateway-c", false);
        var gatewayPin = Pin(gatewayCertificate);
        var otherPin = Pin(otherCertificate);
        var otherTargetPin = Pin(otherTargetCertificate);
        var gatewayIdentity = new PrivateAccessGatewayIdentity("tenant-a", "target-a", "gateway-a", 1, 1);
        var otherIdentity = new PrivateAccessGatewayIdentity("tenant-b", "target-a", "gateway-b", 1, 1);
        var otherTargetIdentity = new PrivateAccessGatewayIdentity("tenant-a", "target-b", "gateway-c", 1, 1);
        var connectorPort = FreePort();
        var publicPort = FreePort();
        var revoked = false;
        var started = 0;
        var renewed = 0;
        var closeCount = 0;
        var redeemed = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
        var reachedRenewal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRenewal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource<PrivateAccessSessionCloseReason>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PrivateAccess:Enabled"] = "true",
            ["PrivateAccess:GatewayPort"] = connectorPort.ToString(CultureInfo.InvariantCulture),
            ["PrivateAccess:GatewayImage"] = "test/gateway:1",
            ["PrivateAccess:BrokerUri"] = $"https://127.0.0.1:{connectorPort}",
            ["PrivateAccess:BrokerAddresses:0"] = "192.0.2.8",
            ["Kestrel:Endpoints:PrivateAccessGateway:Url"] = $"https://127.0.0.1:{connectorPort}",
            ["Kestrel:Endpoints:Public:Url"] = $"https://127.0.0.1:{publicPort}"
        });
        builder.WebHost.ConfigureKestrel(options => options.ConfigureHttpsDefaults(https =>
            https.ServerCertificate = serverCertificate));
        builder.ConfigurePrivateAccessListener();
        builder.Services.AddPrivateAccessRuntime(builder.Configuration);
        builder.Services.AddSingleton<ResolvePrivateAccessGatewayIdentity>((pin, _) =>
            Task.FromResult(pin == gatewayPin && !revoked ? gatewayIdentity :
                pin == otherPin ? otherIdentity :
                pin == otherTargetPin ? otherTargetIdentity : null));
        builder.Services.AddSingleton<RecordPrivateAccessGatewayContact>((_, _, _) =>
            Task.FromResult(true));
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
        builder.Services.AddSingleton<ClosePrivateAccessSession>((_, reason, _) =>
        {
            Interlocked.Increment(ref closeCount);
            closed.TrySetResult(reason);
            return Task.FromResult<ClosePrivateAccessSessionOutcome>(new PrivateAccessSessionClosed());
        });
        builder.Services.AddSingleton<LoadExpiredPrivateAccessSessionCleanupPage>((_, _, _) =>
            Task.FromResult<IReadOnlyList<PrivateAccessSessionCleanupSnapshot>>([]));
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
        Assert.NotNull(app.Services.GetRequiredService<PrivateAccessGatewayRegistry>()
            .Find("tenant-a", "target-a"));
        Assert.NotNull(app.Services.GetRequiredService<PrivateAccessGatewayRegistry>()
            .Find("tenant-b", "target-a"));

        using var user = Socket(serverCertificate, bearer: "user-ticket");
        var connectingUser = user.ConnectAsync(userStreamUri, timeout.Token);
        var open = await PrivateAccessFrames.ReceiveAsync(control, timeout.Token);
        var ticket = PrivateAccessGatewayProtocol.ReadOpenStream(open);
        using var wrongTenant = Socket(serverCertificate, otherCertificate, ticket);
        await Assert.ThrowsAsync<WebSocketException>(() =>
            wrongTenant.ConnectAsync(gatewayStreamUri, timeout.Token));
        using var wrongTarget = Socket(serverCertificate, otherTargetCertificate, ticket);
        await Assert.ThrowsAsync<WebSocketException>(() =>
            wrongTarget.ConnectAsync(gatewayStreamUri, timeout.Token));
        using var gatewayStream = Socket(serverCertificate, gatewayCertificate, ticket);
        await gatewayStream.ConnectAsync(gatewayStreamUri, timeout.Token);
        using var replay = Socket(serverCertificate, gatewayCertificate, ticket);
        await Assert.ThrowsAsync<WebSocketException>(() =>
            replay.ConnectAsync(gatewayStreamUri, timeout.Token));
        var destination = PrivateAccessGatewayProtocol.ReadDestination(
            await PrivateAccessFrames.ReceiveAsync(gatewayStream, timeout.Token));
        Assert.Equal("database.runtime-a.svc", destination.ServiceDns);
        Assert.Equal(5432, destination.ServicePort);
        await connectingUser;

        using var backend = new TcpListener(IPAddress.Loopback, 0);
        backend.Start();
        var gatewayRun = new PrivateAccessTcpRelay(TimeProvider.System).RunAsync(gatewayStream,
            "127.0.0.1", ((IPEndPoint)backend.LocalEndpoint).Port, timeout.Token);
        await reachedRenewal.Task.WaitAsync(timeout.Token);
        Assert.False(backend.Pending());
        await PrivateAccessFrames.SendAsync(user, PrivateAccessFrameKind.Data,
            Encoding.UTF8.GetBytes("request"), timeout.Token);
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
            secondGatewayStream, "127.0.0.1",
            ((IPEndPoint)secondBackend.LocalEndpoint).Port, timeout.Token);
        await PrivateAccessFrames.SendAsync(secondUser, PrivateAccessFrameKind.Data,
            Encoding.UTF8.GetBytes("second"), timeout.Token);
        using var secondConnection = await secondBackend.AcceptTcpClientAsync(timeout.Token);
        var secondRequest = new byte[6];
        await secondConnection.GetStream().ReadExactlyAsync(secondRequest, timeout.Token);
        Assert.Equal("second", Encoding.UTF8.GetString(secondRequest));
        Assert.Equal(2, started);

        revoked = true;
        await PrivateAccessFrames.SendAsync(control, PrivateAccessFrameKind.LeaseChallenge,
            RandomNumberGenerator.GetBytes(32), timeout.Token);
        await closed.Task.WaitAsync(timeout.Token);
        await SpinUntilAsync(() => Volatile.Read(ref closeCount) == 2, timeout.Token);
        Assert.Null(app.Services.GetRequiredService<PrivateAccessGatewayRegistry>()
            .Find("tenant-a", "target-a"));
        var terminal = await Record.ExceptionAsync(async () =>
            await gatewayRun.WaitAsync(timeout.Token));
        Assert.True(terminal is null or OperationCanceledException or IOException or WebSocketException,
            terminal?.GetType().Name);
        var secondTerminal = await Record.ExceptionAsync(async () =>
            await secondGatewayRun.WaitAsync(timeout.Token));
        Assert.True(secondTerminal is null or OperationCanceledException or IOException or WebSocketException,
            secondTerminal?.GetType().Name);

        revoked = false;
        using var recovered = Socket(serverCertificate, gatewayCertificate);
        await recovered.ConnectAsync(controlUri, timeout.Token);
        await ChallengeAsync(recovered, timeout.Token);
        Assert.NotNull(app.Services.GetRequiredService<PrivateAccessGatewayRegistry>()
            .Find("tenant-a", "target-a"));
        recovered.Abort();
        await SpinUntilAsync(() => app.Services.GetRequiredService<PrivateAccessGatewayRegistry>()
            .Find("tenant-a", "target-a") is null, timeout.Token);
        await app.StopAsync(timeout.Token);
    }

    private static PrivateAccessLeaseSnapshot Lease(string sessionId) => new(
        "tenant-a", "user-a", "grant-a", sessionId, "postgresql", "resource-a",
        "deployment-a", "endpoint-a", "target-a", "cell-a", "scope-a", "runtime-a",
        "database", NetworkPort.Create(5432), NetworkPort.Create(5432), 1,
        DateTime.UtcNow.AddMinutes(1), DateTime.UtcNow.AddMinutes(2));

    private static async Task ChallengeAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        await PrivateAccessFrames.SendAsync(socket, PrivateAccessFrameKind.LeaseChallenge,
            nonce, cancellationToken);
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
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is not null && CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(certificate.GetRawCertData()), SHA256.HashData(server.RawData));
        return socket;
    }

    private static X509Certificate2 Certificate(string name, bool server)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") }, false));
        if (server)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
        }
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
