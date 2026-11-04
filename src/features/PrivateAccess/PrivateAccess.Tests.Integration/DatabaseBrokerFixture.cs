using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;
using k8s;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Primitives;
using Pole.Features.PrivateAccess.Runtime;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Workloads;
using Xunit;

namespace Pole.Features.PrivateAccess.Tests.Integration;

/// <summary>Real user and gateway WebSocket transport against containerized databases.</summary>
internal sealed class DatabaseBrokerFixture : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly X509Certificate2 serverCertificate;
    private readonly X509Certificate2 gatewayCertificate;
    private readonly string peerCertificatePath;
    private readonly ClientWebSocket control;
    private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(60));
    private readonly ConcurrentDictionary<string, PrivateAccessLeaseSnapshot> sessions = new();
    private readonly ConcurrentQueue<PrivateAccessSessionCloseReason> closeReasons = new();
    private readonly Channel<string> gatewayTickets = Channel.CreateUnbounded<string>();
    private readonly ConcurrentQueue<byte[]> challenges = new();
    private readonly int backendPort;
    private readonly Uri publicOrigin;
    private readonly Uri gatewayOrigin;
    private Task controlReader = Task.CompletedTask;
    private Task controlRenewal = Task.CompletedTask;
    private int sessionSequence;
    private int started;
    private int renewed;

    private DatabaseBrokerFixture(
        WebApplication app,
        X509Certificate2 serverCertificate,
        X509Certificate2 gatewayCertificate,
        string peerCertificatePath,
        ClientWebSocket control,
        Uri publicOrigin,
        Uri gatewayOrigin,
        int backendPort)
    {
        this.app = app;
        this.serverCertificate = serverCertificate;
        this.gatewayCertificate = gatewayCertificate;
        this.peerCertificatePath = peerCertificatePath;
        this.control = control;
        this.publicOrigin = publicOrigin;
        this.gatewayOrigin = gatewayOrigin;
        this.backendPort = backendPort;
    }

    public int StartedSessions => Volatile.Read(ref started);
    public int RenewedSessions => Volatile.Read(ref renewed);
    public string CloseReasons => string.Join(",", closeReasons);

    public static async Task<DatabaseBrokerFixture> StartAsync(int backendPort)
    {
        var serverCertificate = Certificate("localhost", true);
        var gatewayCertificate = Certificate("gateway", false);
        var pin = Convert.ToHexString(SHA256.HashData(gatewayCertificate.RawData))
            .ToLowerInvariant();
        var gatewayPort = FreePort();
        var publicPort = FreePort();
        var internalPort = FreePort();
        var internalOrigin = $"https://127.0.0.1:{internalPort}";
        var peerCertificatePath = Path.Combine(Path.GetTempPath(),
            $"pole-peer-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(peerCertificatePath, serverCertificate.Export(X509ContentType.Pfx));
        var ownerSync = new object();
        PrivateAccessBrokerOwnerLease? owner = null;
        long ownerEpoch = 0;
        var gatewayIdentity = new PrivateAccessGatewayIdentity(
            "tenant-a", "target-a", "gateway-a", 1, 1);
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PrivateAccess:Enabled"] = "true",
            ["PrivateAccess:GatewayPort"] = gatewayPort.ToString(CultureInfo.InvariantCulture),
            ["PrivateAccess:GatewayImage"] = "test/gateway:1",
            ["PrivateAccess:BrokerUri"] = $"https://127.0.0.1:{publicPort}",
            ["PrivateAccess:BrokerAddresses:0"] = "192.0.2.8",
            ["PrivateAccess:InternalPort"] = internalPort.ToString(CultureInfo.InvariantCulture),
            ["PrivateAccess:InstanceId"] = "test-instance",
            ["PrivateAccess:InternalOrigin"] = internalOrigin,
            ["PrivateAccess:InternalPeerOrigins:0"] = internalOrigin,
            ["PrivateAccess:InternalCertificatePath"] = peerCertificatePath,
            ["PrivateAccess:InternalCertificateSha256Pin"] = Convert.ToHexString(
                SHA256.HashData(serverCertificate.RawData)).ToLowerInvariant(),
            ["Kestrel:Endpoints:PrivateAccessGateway:Url"] = $"https://127.0.0.1:{gatewayPort}",
            ["Kestrel:Endpoints:PrivateAccessInternal:Url"] = internalOrigin,
            ["Kestrel:Endpoints:Public:Url"] = $"https://127.0.0.1:{publicPort}"
        });
        builder.WebHost.ConfigureKestrel(options => options.ConfigureHttpsDefaults(https =>
            https.ServerCertificate = serverCertificate));
        builder.ConfigurePrivateAccessListener();
        builder.Services.AddPrivateAccessRuntime(builder.Configuration);
        builder.Services.AddSingleton<ClaimPrivateAccessBrokerOwner>((request, _) =>
        {
            lock (ownerSync)
            {
                owner = new PrivateAccessBrokerOwnerLease(request.Gateway, request.InstanceId, request.InternalOrigin,
                    ++ownerEpoch, DateTime.UtcNow.AddSeconds(10));
                return Task.FromResult<ClaimPrivateAccessBrokerOwnerOutcome>(owner);
            }
        });
        builder.Services.AddSingleton<RenewPrivateAccessBrokerOwner>((lease, _) =>
        {
            lock (ownerSync)
            {
                if (owner?.Epoch != lease.Epoch)
                    return Task.FromResult(false);
                owner = owner with { ExpiresAtUtc = DateTime.UtcNow.AddSeconds(10) };
                return Task.FromResult(true);
            }
        });
        builder.Services.AddSingleton<ReleasePrivateAccessBrokerOwner>((lease, _) =>
        {
            lock (ownerSync)
            {
                if (owner?.Epoch == lease.Epoch)
                    owner = null;
                return Task.CompletedTask;
            }
        });
        builder.Services.AddSingleton<LoadPrivateAccessBrokerOwner>((tenant, target, _) =>
        {
            lock (ownerSync)
                return Task.FromResult(owner is { } current &&
                    current.Gateway.TenantId == tenant &&
                    current.Gateway.DeploymentTargetId == target &&
                    current.ExpiresAtUtc > DateTime.UtcNow ? current : null);
        });
        builder.Services.AddSingleton<IKubernetesClientProvider>(new UnusedKubernetesProvider());
        builder.Services.AddSingleton<LookupDeploymentTarget>((_, _) =>
            throw new InvalidOperationException("Gateway reconciliation has no desired routes."));
        builder.Services.AddSingleton<EnsureRuntimeCellIngressBaseline>((request, _) =>
            throw new InvalidOperationException("Gateway reconciliation has no desired routes."));
        builder.Services.AddSingleton<LoadPrivateAccessGatewayDesiredStates>(_ =>
            Task.FromResult<IReadOnlyList<PrivateAccessGatewayDesiredState>>([]));
        builder.Services.AddSingleton<LoadPrivateAccessGatewayRegistrations>(_ =>
            Task.FromResult<IReadOnlyList<
                Pole.Features.PrivateAccess.Contracts.PrivateAccessGatewayRegistration>>([]));
        builder.Services.AddSingleton<SavePrivateAccessGatewayRegistration>((request, _) =>
            throw new InvalidOperationException("Gateway registration is not used in transport tests."));
        builder.Services.AddSingleton<RemovePrivateAccessGatewayRegistration>((_, _) =>
            throw new InvalidOperationException("Gateway registration is not used in transport tests."));
        builder.Services.AddSingleton<ResolvePrivateAccessGatewayIdentity>((fingerprint, _) =>
            Task.FromResult(fingerprint == pin ? gatewayIdentity : null));
        builder.Services.AddSingleton<RecordPrivateAccessGatewayContact>((_, _, _) =>
            Task.FromResult(true));

        DatabaseBrokerFixture? fixture = null;
        builder.Services.AddSingleton<LocatePrivateAccessTicket>((ticket, _) =>
            Task.FromResult(fixture!.sessions.TryGetValue(ticket, out var session)
                ? new PrivateAccessTicketRoute(session.TenantId, session.DeploymentTargetId)
                : null));
        builder.Services.AddSingleton<RedeemPrivateAccessSession>((ticket, _) =>
        {
            var found = fixture!.sessions.TryRemove(ticket, out var lease);
            if (found)
                fixture.sessions[lease!.SessionId] = lease;
            return Task.FromResult<RedeemPrivateAccessSessionOutcome>(found
                ? lease!
                : new PrivateAccessTicketUsed());
        });
        builder.Services.AddSingleton<RenewPrivateAccessSession>((sessionId, _) =>
        {
            Interlocked.Increment(ref fixture!.renewed);
            return Task.FromResult<RenewPrivateAccessSessionOutcome>(
                fixture.sessions.TryGetValue(sessionId, out var lease)
                    ? lease
                    : new PrivateAccessSessionUnavailable());
        });
        builder.Services.AddSingleton<MarkPrivateAccessSessionStarted>((_, _) =>
        {
            Interlocked.Increment(ref fixture!.started);
            return Task.FromResult<MarkPrivateAccessSessionStartedOutcome>(
                new PrivateAccessSessionStarted());
        });
        builder.Services.AddSingleton<ClosePrivateAccessSession>((sessionId, reason, cancellationToken) =>
        {
            fixture!.sessions.TryRemove(sessionId, out _);
            fixture.closeReasons.Enqueue(reason);
            return Task.FromResult<ClosePrivateAccessSessionOutcome>(
                new PrivateAccessSessionClosed());
        });
        builder.Services.AddSingleton<LoadExpiredPrivateAccessSessionCleanupPage>((_, _, _) =>
            Task.FromResult<IReadOnlyList<PrivateAccessSessionCleanupSnapshot>>([]));
        builder.Services.AddSingleton<MarkPrivateAccessSessionCleaned>((_, _) =>
            Task.FromResult<MarkPrivateAccessSessionCleanedOutcome>(
                new PrivateAccessSessionCleaned()));
        var app = builder.Build();
        app.UsePrivateAccessRuntime();
        app.MapPrivateAccessGatewayControlEndpoint();
        app.MapPrivateAccessGatewayStreamEndpoint();
        app.MapPrivateAccessStreamEndpoint();
        await app.StartAsync();

        var control = Socket(serverCertificate, gatewayCertificate);
        await control.ConnectAsync(
            new Uri($"wss://127.0.0.1:{gatewayPort}/private-access/gateways"),
            CancellationToken.None);
        var initial = RandomNumberGenerator.GetBytes(32);
        await PrivateAccessFrames.SendAsync(
            control, PrivateAccessFrameKind.LeaseChallenge, initial, CancellationToken.None);
        var accepted = await PrivateAccessFrames.ReceiveAsync(control, CancellationToken.None);
        Assert.Equal(PrivateAccessFrameKind.LeaseAccepted, accepted.Kind);
        Assert.Equal(initial, accepted.Payload.ToArray());

        fixture = new DatabaseBrokerFixture(
            app, serverCertificate, gatewayCertificate, peerCertificatePath, control,
            new Uri($"wss://127.0.0.1:{publicPort}"),
            new Uri($"wss://127.0.0.1:{gatewayPort}"),
            backendPort);
        fixture.controlReader = fixture.ReadControlAsync();
        fixture.controlRenewal = fixture.RenewControlAsync();
        return fixture;
    }

    private async Task ReadControlAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var frame = await PrivateAccessFrames.ReceiveAsync(control, lifetime.Token);
                switch (frame.Kind)
                {
                    case PrivateAccessFrameKind.Data:
                        var ticket = PrivateAccessGatewayProtocol.ReadOpenStream(frame);
                        await gatewayTickets.Writer.WriteAsync(ticket, lifetime.Token);
                        break;
                    case PrivateAccessFrameKind.LeaseAccepted:
                        if (!challenges.TryDequeue(out var challenge) ||
                            !challenge.AsSpan().SequenceEqual(frame.Payload.Span))
                            throw new InvalidDataException("Gateway control lease was rejected.");
                        break;
                    default:
                        throw new InvalidDataException("Unexpected gateway control frame.");
                }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or
            IOException or WebSocketException or ObjectDisposedException or InvalidDataException)
        {
            gatewayTickets.Writer.TryComplete(error);
        }
    }

    private async Task RenewControlAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        using var writer = new PrivateAccessFrameWriter(control);
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                var challenge = RandomNumberGenerator.GetBytes(32);
                challenges.Enqueue(challenge);
                await writer.SendAsync(
                    PrivateAccessFrameKind.LeaseChallenge, challenge, lifetime.Token);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or
            IOException or WebSocketException or ObjectDisposedException or InvalidDataException)
        {
            gatewayTickets.Writer.TryComplete(error);
        }
    }

    public async Task<DatabaseLocalForward> ForwardAsync()
    {
        var forward = new DatabaseLocalForward(this);
        await forward.StartAsync();
        return forward;
    }

    internal string AllocateSession()
    {
        var id = Interlocked.Increment(ref sessionSequence).ToString(CultureInfo.InvariantCulture);
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        sessions[ticket] = new PrivateAccessLeaseSnapshot(
            "tenant-a", "user-a", "grant-a", $"session-{id}", "postgresql", "resource-a",
            "deployment-a", "endpoint-a", "target-a", "cell-a", "scope-a", "runtime-a",
            "database", NetworkPort.Create(backendPort), NetworkPort.Create(backendPort), 1,
            DateTime.UtcNow.AddMinutes(1), DateTime.UtcNow.AddMinutes(2));
        return ticket;
    }

    internal ClientWebSocket PublicSocket(string ticket)
    {
        var socket = Socket(serverCertificate);
        socket.Options.SetRequestHeader("Authorization", $"Bearer {ticket}");
        return socket;
    }

    internal ClientWebSocket GatewaySocket(string ticket)
    {
        var socket = Socket(serverCertificate, gatewayCertificate);
        socket.Options.SetRequestHeader("Authorization", $"Bearer {ticket}");
        return socket;
    }

    internal Uri StreamUri => new(publicOrigin, "/private-access/streams");
    internal Uri GatewayStreamUri => new(gatewayOrigin, "/private-access/gateway-streams");
    internal ChannelReader<string> GatewayTickets => gatewayTickets.Reader;
    internal int BackendPort => backendPort;
    internal CancellationToken Token => lifetime.Token;

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        control.Abort();
        try { await Task.WhenAll(controlReader, controlRenewal); }
        catch (Exception error) when (error is IOException or WebSocketException or
            OperationCanceledException or ObjectDisposedException or InvalidDataException)
        { }
        control.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        serverCertificate.Dispose();
        gatewayCertificate.Dispose();
        File.Delete(peerCertificatePath);
        lifetime.Dispose();
    }

    private static ClientWebSocket Socket(
        X509Certificate2 server,
        X509Certificate2? client = null)
    {
        var socket = new ClientWebSocket();
        if (client is not null)
            socket.Options.ClientCertificates.Add(client);
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is not null && CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(certificate.GetRawCertData()),
                SHA256.HashData(server.RawData));
        return socket;
    }

    private static X509Certificate2 Certificate(string name, bool server)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection
            {
                new(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")
            }, false));
        if (server)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
        }
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(
            certificate.Export(X509ContentType.Pfx), null);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class UnusedKubernetesProvider : IKubernetesClientProvider
    {
        public Task<IKubernetes> GetAsync(
            string id,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No gateway resources are reconciled in transport tests.");

        public Task<IKubernetes> GetForCleanupAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default) =>
            GetAsync(deploymentTargetId, cancellationToken);

        public Task<IKubernetes> GetDefaultAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No default gateway target is used.");
    }
}

internal sealed class DatabaseLocalForward(DatabaseBrokerFixture broker) : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(broker.Token);
    private readonly SemaphoreSlim setupGate = new(1, 1);
    private readonly ConcurrentBag<Task> sessions = [];
    private readonly ConcurrentQueue<string> errors = new();
    private Task? accepting;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public string Diagnostics => string.Join(",", errors);

    public Task StartAsync()
    {
        listener.Start();
        accepting = AcceptAsync();
        return Task.CompletedTask;
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                sessions.Add(RunSessionAsync(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task RunSessionAsync(TcpClient local)
    {
        var stage = "accepted";
        using (local)
        using (var client = broker.PublicSocket(broker.AllocateSession()))
        {
            try
            {
                stage = "waiting-for-setup";
                await setupGate.WaitAsync(lifetime.Token);
                ClientWebSocket gateway;
                try
                {
                    stage = "connecting-stream";
                    var connecting = client.ConnectAsync(broker.StreamUri, lifetime.Token);
                    var ticket = await broker.GatewayTickets.ReadAsync(lifetime.Token);
                    gateway = broker.GatewaySocket(ticket);
                    try
                    {
                        stage = "connecting-gateway";
                        await gateway.ConnectAsync(broker.GatewayStreamUri, lifetime.Token);
                        _ = PrivateAccessGatewayProtocol.ReadDestination(
                            await PrivateAccessFrames.ReceiveAsync(gateway, lifetime.Token));
                        await connecting;
                    }
                    catch
                    {
                        gateway.Dispose();
                        throw;
                    }
                }
                finally
                {
                    setupGate.Release();
                }
                using (gateway)
                {
                    stage = "pumping";
                    var gatewayRun = new PrivateAccessTcpRelay(TimeProvider.System).RunAsync(gateway,
                        "127.0.0.1", broker.BackendPort, lifetime.Token);
                    var localStream = local.GetStream();
                    var outgoing = PumpToBrokerAsync(localStream, client, lifetime.Token);
                    var incoming = PumpFromBrokerAsync(localStream, local, client, lifetime.Token);
                    await Task.WhenAny(Task.WhenAll(outgoing, incoming), gatewayRun);
                    client.Abort();
                    gateway.Abort();
                    await ObserveAsync(outgoing);
                    await ObserveAsync(incoming);
                    await ObserveAsync(gatewayRun);
                }
            }
            catch (Exception error) when (error is IOException or WebSocketException or
                OperationCanceledException or SocketException or ObjectDisposedException or
                InvalidDataException)
            {
                if (!lifetime.IsCancellationRequested)
                    errors.Enqueue($"{stage}:{error.GetType().Name}");
                client.Abort();
            }
        }
    }

    private static async Task PumpToBrokerAsync(NetworkStream local, WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[PrivateAccessFrames.MaximumPayloadLength];
        while (true)
        {
            var count = await local.ReadAsync(buffer, ct);
            if (count == 0)
            {
                await PrivateAccessFrames.SendAsync(socket, PrivateAccessFrameKind.HalfClose,
                    ReadOnlyMemory<byte>.Empty, ct);
                return;
            }
            await PrivateAccessFrames.SendAsync(socket, PrivateAccessFrameKind.Data,
                buffer.AsMemory(0, count), ct);
        }
    }

    private static async Task PumpFromBrokerAsync(
        NetworkStream local,
        TcpClient localClient,
        WebSocket socket,
        CancellationToken ct)
    {
        while (true)
        {
            var frame = await PrivateAccessFrames.ReceiveAsync(socket, ct);
            switch (frame.Kind)
            {
                case PrivateAccessFrameKind.Data:
                    await local.WriteAsync(frame.Payload, ct);
                    break;
                case PrivateAccessFrameKind.HalfClose:
                    localClient.Client.Shutdown(SocketShutdown.Send);
                    return;
                case PrivateAccessFrameKind.Closed:
                    return;
                default:
                    throw new InvalidDataException("Unexpected client frame.");
            }
        }
    }

    private async Task ObserveAsync(Task task)
    {
        try { await task; }
        catch (Exception error) when (error is IOException or WebSocketException or
            OperationCanceledException or SocketException or ObjectDisposedException or
            InvalidDataException)
        {
            if (!lifetime.IsCancellationRequested)
                errors.Enqueue($"pump:{error.GetType().Name}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        listener.Stop();
        if (accepting is not null)
            await accepting;
        await Task.WhenAll(sessions.ToArray());
        setupGate.Dispose();
        lifetime.Dispose();
    }
}
