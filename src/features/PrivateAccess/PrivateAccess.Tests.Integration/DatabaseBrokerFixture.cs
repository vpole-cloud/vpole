using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
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

/// <summary>A real broker and worker transport with fake admission and Kubernetes writes.</summary>
internal sealed class DatabaseBrokerFixture : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly Kubernetes kubernetes;
    private readonly X509Certificate2 serverCertificate;
    private readonly X509Certificate2 connectorCertificate;
    private readonly ClientWebSocket connector;
    private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(45));
    private readonly ConcurrentDictionary<string, PrivateAccessLeaseSnapshot> sessions = new();
    private readonly ConcurrentQueue<PrivateAccessSessionCloseReason> closeReasons = new();
    private readonly FakeKubernetes api;
    private readonly int backendPort;
    private readonly Uri publicOrigin;
    private readonly Task connectorLease;
    private int sessionSequence;
    private int started;
    private int renewed;

    private DatabaseBrokerFixture(
        WebApplication app,
        Kubernetes kubernetes,
        FakeKubernetes api,
        X509Certificate2 serverCertificate,
        X509Certificate2 connectorCertificate,
        ClientWebSocket connector,
        Uri publicOrigin,
        int backendPort,
        Task connectorLease)
    {
        this.app = app;
        this.kubernetes = kubernetes;
        this.api = api;
        this.serverCertificate = serverCertificate;
        this.connectorCertificate = connectorCertificate;
        this.connector = connector;
        this.publicOrigin = publicOrigin;
        this.backendPort = backendPort;
        this.connectorLease = connectorLease;
    }

    public int StartedSessions => Volatile.Read(ref started);
    public int RenewedSessions => Volatile.Read(ref renewed);
    public string CloseReasons => string.Join(",", closeReasons);

    public static async Task<DatabaseBrokerFixture> StartAsync(int backendPort)
    {
        var serverCertificate = Certificate("localhost", true);
        var connectorCertificate = Certificate("untrusted-subject", false);
        var fingerprint = Convert.ToHexString(SHA256.HashData(connectorCertificate.RawData));
        var connectorPort = FreePort();
        var publicPort = FreePort();
        var api = new FakeKubernetes();
        var kubernetes = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://kubernetes.test" }, api);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PrivateAccess:Enabled"] = "true",
            ["PrivateAccess:ConnectorPort"] = connectorPort.ToString(CultureInfo.InvariantCulture),
            ["PrivateAccess:WorkerImage"] = "test/worker:1",
            ["PrivateAccess:BrokerUri"] = $"https://127.0.0.1:{publicPort}",
            ["PrivateAccess:BrokerAddresses:0"] = "192.0.2.8",
            ["Kestrel:Endpoints:PrivateAccessConnector:Url"] = $"https://127.0.0.1:{connectorPort}",
            ["Kestrel:Endpoints:Public:Url"] = $"https://127.0.0.1:{publicPort}"
        });
        builder.WebHost.ConfigureKestrel(options => options.ConfigureHttpsDefaults(https =>
            https.ServerCertificate = serverCertificate));
        builder.ConfigurePrivateAccessListener();
        builder.Services.AddPrivateAccessRuntime(builder.Configuration);
        builder.Services.AddSingleton<IKubernetesClientProvider>(new FixedKubernetesProvider(kubernetes));
        builder.Services.AddSingleton<LookupDeploymentTarget>((id, _) =>
            Task.FromResult<LookupDeploymentTargetOutcome>(Target(id)));
        builder.Services.AddSingleton<EnsureRuntimeCellIngressBaseline>((_, _, _, _, _) =>
            Task.FromResult<EnsureRuntimeCellIngressBaselineOutcome>(new RuntimeCellIngressBaselineReady()));
        builder.Services.AddSingleton<ResolveDeploymentTargetConnector>((pin, _) =>
            Task.FromResult<ResolveDeploymentTargetConnectorOutcome>(pin == fingerprint
                ? new DeploymentTargetConnectorIdentity("target-a", "connector-a", 1, 1)
                : new DeploymentTargetConnectorRejected()));
        builder.Services.AddSingleton<RecordDeploymentTargetConnectorContact>((_, _) =>
            Task.FromResult<RecordDeploymentTargetConnectorContactOutcome>(
                new DeploymentTargetConnectorContactRecorded()));

        DatabaseBrokerFixture? fixture = null;
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
        builder.Services.AddSingleton<ClosePrivateAccessSession>((sessionId, reason, _) =>
        {
            fixture!.sessions.TryRemove(sessionId, out var removedLease);
            fixture.closeReasons.Enqueue(reason);
            return Task.FromResult<ClosePrivateAccessSessionOutcome>(new PrivateAccessSessionClosed());
        });
        builder.Services.AddSingleton<RecordPrivateAccessWorkerNamespace>((_, _, _) =>
            Task.FromResult<RecordPrivateAccessWorkerNamespaceOutcome>(
                new PrivateAccessWorkerNamespaceRecorded()));
        builder.Services.AddSingleton<LoadExpiredPrivateAccessSessionCleanupPage>((_, _, _) =>
            Task.FromResult<IReadOnlyList<PrivateAccessSessionCleanupSnapshot>>([]));
        builder.Services.AddSingleton<MarkPrivateAccessSessionCleaned>((_, _) =>
            Task.FromResult<MarkPrivateAccessSessionCleanedOutcome>(
                new PrivateAccessSessionCleaned()));
        var app = builder.Build();
        app.UsePrivateAccessRuntime();
        app.MapPrivateAccessConnectorEndpoint();
        app.MapPrivateAccessWorkerEndpoint();
        app.MapPrivateAccessStreamEndpoint();
        await app.StartAsync();

        var connector = Socket(serverCertificate, connectorCertificate);
        await connector.ConnectAsync(
            new Uri($"wss://127.0.0.1:{connectorPort}/private-access/connectors"),
            CancellationToken.None);
        var initial = RandomNumberGenerator.GetBytes(32);
        await PrivateAccessFrames.SendAsync(connector, PrivateAccessFrameKind.LeaseChallenge, initial,
            CancellationToken.None);
        var accepted = await PrivateAccessFrames.ReceiveAsync(connector, CancellationToken.None);
        Assert.Equal(PrivateAccessFrameKind.LeaseAccepted, accepted.Kind);
        Assert.Equal(initial, accepted.Payload.ToArray());

        var renewals = ConnectorLeaseAsync(connector);
        fixture = new DatabaseBrokerFixture(app, kubernetes, api, serverCertificate,
            connectorCertificate, connector, new Uri($"wss://127.0.0.1:{publicPort}"), backendPort,
            renewals);
        return fixture;

        async Task ConnectorLeaseAsync(ClientWebSocket socket)
        {
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), fixture?.lifetime.Token ?? CancellationToken.None);
                    var nonce = RandomNumberGenerator.GetBytes(32);
                    await PrivateAccessFrames.SendAsync(socket, PrivateAccessFrameKind.LeaseChallenge,
                        nonce, fixture?.lifetime.Token ?? CancellationToken.None);
                    var reply = await PrivateAccessFrames.ReceiveAsync(socket,
                        fixture?.lifetime.Token ?? CancellationToken.None);
                    if (reply.Kind != PrivateAccessFrameKind.LeaseAccepted ||
                        !nonce.AsSpan().SequenceEqual(reply.Payload.Span))
                        throw new InvalidDataException("Connector lease was rejected.");
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (WebSocketException) { }
            catch (ObjectDisposedException) { }
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

    internal Uri StreamUri => new(publicOrigin, "/private-access/streams");
    internal Uri WorkerUri => new(publicOrigin, "/private-access/workers");
    internal ChannelReader<string> WorkerTickets => api.Tickets.Reader;
    internal int BackendPort => backendPort;
    internal CancellationToken Token => lifetime.Token;

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        connector.Abort();
        try { await connectorLease; }
        catch (Exception error) when (error is IOException or WebSocketException or
            OperationCanceledException or ObjectDisposedException)
        { }
        connector.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        kubernetes.Dispose();
        serverCertificate.Dispose();
        connectorCertificate.Dispose();
        lifetime.Dispose();
    }

    private static DeploymentTargetLookupDto Target(string id) => new(
        id, "Target", "cluster-a", "Kubernetes", 1,
        new DeploymentTargetWorkloadConfigurationLookupDto(1, null,
            new DeploymentTargetNetworkSourcesDto(
                ImmutableDictionary<string, DeploymentTargetNetworkSourceDto>.Empty)),
        1, true, true, false, "Healthy")
    {
        NetworkPolicy = new DeploymentTargetNetworkPolicyDto(
            NetworkPolicyEnforcementStatus.Supported,
            NetworkPolicyReadiness.EnforcementAttested,
            NetworkPolicyReason.OperatorAttestedSupported,
            DateTime.UtcNow,
            NetworkPolicyEvidenceSource.OperatorAttestation,
            false)
    };

    private static ClientWebSocket Socket(X509Certificate2 server, X509Certificate2? client = null)
    {
        var socket = new ClientWebSocket();
        if (client is not null)
            socket.Options.ClientCertificates.Add(client);
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

    private sealed class FixedKubernetesProvider(IKubernetes client) : IKubernetesClientProvider
    {
        public Task<IKubernetes> GetAsync(string id, CancellationToken ct = default)
        {
            Assert.Equal("target-a", id);
            return Task.FromResult(client);
        }

        public Task<IKubernetes> GetDefaultAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("Target-specific Kubernetes client required.");
    }

    private sealed class FakeKubernetes : DelegatingHandler
    {
        private readonly ConcurrentDictionary<string, string> objects = new();
        public Channel<string> Tickets { get; } = Channel.CreateUnbounded<string>();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                var metadata = body["metadata"]!.AsObject();
                metadata["uid"] = "uid-owned";
                metadata["resourceVersion"] = "1";
                var key = $"{path}/{metadata["name"]!.GetValue<string>()}";
                if (!objects.TryAdd(key, body.ToJsonString()))
                    return Response(request, HttpStatusCode.Conflict, "{}");
                if (path.EndsWith("/secrets", StringComparison.Ordinal))
                    await Tickets.Writer.WriteAsync(body["stringData"]!["ticket"]!.GetValue<string>(),
                        cancellationToken);
                return Response(request, HttpStatusCode.Created, body.ToJsonString());
            }
            if (request.Method == HttpMethod.Get)
                return objects.TryGetValue(path, out var value)
                    ? Response(request, HttpStatusCode.OK, value)
                    : Response(request, HttpStatusCode.NotFound, "{}");
            if (request.Method == HttpMethod.Delete)
            {
                objects.TryRemove(path, out _);
                return Response(request, HttpStatusCode.OK, "{}");
            }
            throw new InvalidOperationException($"Unexpected Kubernetes request {request.Method} {path}");
        }

        private static HttpResponseMessage Response(HttpRequestMessage request, HttpStatusCode status, string body) =>
            new(status)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
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
                ClientWebSocket worker;
                try
                {
                    stage = "connecting-stream";
                    var connecting = client.ConnectAsync(broker.StreamUri, lifetime.Token);
                    var ticket = await broker.WorkerTickets.ReadAsync(lifetime.Token);
                    worker = broker.PublicSocket(ticket);
                    try
                    {
                        stage = "connecting-worker";
                        await worker.ConnectAsync(broker.WorkerUri, lifetime.Token);
                        await connecting;
                    }
                    catch
                    {
                        worker.Dispose();
                        throw;
                    }
                }
                finally
                {
                    setupGate.Release();
                }
                using (worker)
                {
                    stage = "pumping";
                    var workerRun = new PrivateAccessWorker(TimeProvider.System).RunAsync(worker,
                        "127.0.0.1", broker.BackendPort, lifetime.Token);
                    var localStream = local.GetStream();
                    var outgoing = PumpToBrokerAsync(localStream, client, lifetime.Token);
                    var incoming = PumpFromBrokerAsync(localStream, local, client, lifetime.Token);
                    await Task.WhenAny(Task.WhenAll(outgoing, incoming), workerRun);
                    client.Abort();
                    worker.Abort();
                    await ObserveAsync(outgoing);
                    await ObserveAsync(incoming);
                    await ObserveAsync(workerRun);
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
