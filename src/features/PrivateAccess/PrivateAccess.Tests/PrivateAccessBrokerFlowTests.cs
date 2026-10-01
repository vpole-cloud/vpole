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

namespace Pole.Features.PrivateAccess.Tests;

public sealed class PrivateAccessBrokerFlowTests
{
    [Fact]
    public async Task RealBrokerFlow_UsesOneWorkerTicket_FreshLease_AndOwnedCleanupOnRevocation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var serverCertificate = Certificate("localhost", server: true);
        using var connectorCertificate = Certificate("forged-other-target", server: false);
        var fingerprint = Convert.ToHexString(SHA256.HashData(connectorCertificate.RawData));
        var connectorPort = FreePort();
        var publicPort = FreePort();
        var revoked = false;
        var started = 0;
        var redeemed = 0;
        var renewed = 0;
        var admissionReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var close = new TaskCompletionSource<PrivateAccessSessionCloseReason>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var kube = new RecordingKubernetes();
        using var kubernetes = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://kubernetes.test" }, kube);
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
            Task.FromResult<ResolveDeploymentTargetConnectorOutcome>(
                pin == fingerprint && !revoked
                    ? new DeploymentTargetConnectorIdentity("target-a", "connector-a", 1, 1)
                    : new DeploymentTargetConnectorRejected()));
        builder.Services.AddSingleton<RecordDeploymentTargetConnectorContact>((_, _) =>
            Task.FromResult<RecordDeploymentTargetConnectorContactOutcome>(
                new DeploymentTargetConnectorContactRecorded()));
        builder.Services.AddSingleton<RedeemPrivateAccessSession>((ticket, _) =>
            Task.FromResult<RedeemPrivateAccessSessionOutcome>(
                ticket == "stream-ticket" && Interlocked.Increment(ref redeemed) == 1
                    ? Lease()
                    : new PrivateAccessTicketUsed()));
        builder.Services.AddSingleton<RenewPrivateAccessSession>(async (_, ct) =>
        {
            if (Interlocked.Increment(ref renewed) == 1)
            {
                admissionReached.TrySetResult();
                await releaseAdmission.Task.WaitAsync(ct);
            }
            return revoked ? new PrivateAccessSessionUnavailable() : Lease();
        });
        builder.Services.AddSingleton<MarkPrivateAccessSessionStarted>((_, _) =>
        {
            Interlocked.Increment(ref started);
            return Task.FromResult<MarkPrivateAccessSessionStartedOutcome>(new PrivateAccessSessionStarted());
        });
        builder.Services.AddSingleton<ClosePrivateAccessSession>((_, reason, _) =>
        {
            close.TrySetResult(reason);
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
        await using var app = builder.Build();
        app.UsePrivateAccessRuntime();
        app.MapPrivateAccessConnectorEndpoint();
        app.MapPrivateAccessWorkerEndpoint();
        app.MapPrivateAccessStreamEndpoint();
        await app.StartAsync(timeout.Token);

        var connectorUri = new Uri($"wss://127.0.0.1:{connectorPort}/private-access/connectors");
        var publicUri = new Uri($"wss://127.0.0.1:{publicPort}");
        using var connector = Socket(serverCertificate, connectorCertificate);
        await connector.ConnectAsync(connectorUri, timeout.Token);
        await ChallengeAsync(connector, timeout.Token);
        Assert.NotNull(app.Services.GetRequiredService<PrivateAccessConnectorRegistry>().Find("target-a"));

        using var stream = Socket(serverCertificate, bearer: "stream-ticket");
        var connectingStream = stream.ConnectAsync(new Uri(publicUri, "/private-access/streams"), timeout.Token);
        var ticket = await kube.Ticket.Task.WaitAsync(timeout.Token);
        Assert.Equal(64, ticket.Length);
        await SpinUntilAsync(() => kube.CountCreated("jobs") == 1, timeout.Token);
        Assert.Equal(1, kube.CountCreated("jobs"));
        Assert.Equal(1, kube.CountCreated("secrets"));
        Assert.Equal(3, kube.CountCreated("networkpolicies"));
        Assert.Equal(1, redeemed);

        using var wrong = Socket(serverCertificate, bearer: new string('0', 64));
        await Assert.ThrowsAsync<WebSocketException>(() =>
            wrong.ConnectAsync(new Uri(publicUri, "/private-access/workers"), timeout.Token));
        using var worker = Socket(serverCertificate, bearer: ticket);
        await worker.ConnectAsync(new Uri(publicUri, "/private-access/workers"), timeout.Token);
        using var replay = Socket(serverCertificate, bearer: ticket);
        await Assert.ThrowsAsync<WebSocketException>(() =>
            replay.ConnectAsync(new Uri(publicUri, "/private-access/workers"), timeout.Token));
        await connectingStream;

        using var backend = new TcpListener(IPAddress.Loopback, 0);
        backend.Start();
        var workerRun = new PrivateAccessWorker(TimeProvider.System).RunAsync(worker,
            "127.0.0.1", ((IPEndPoint)backend.LocalEndpoint).Port, timeout.Token);
        await admissionReached.Task.WaitAsync(timeout.Token);
        Assert.False(backend.Pending());
        await PrivateAccessFrames.SendAsync(stream, PrivateAccessFrameKind.Data,
            Encoding.UTF8.GetBytes("request"), timeout.Token);
        await PrivateAccessFrames.SendAsync(stream, PrivateAccessFrameKind.HalfClose,
            ReadOnlyMemory<byte>.Empty, timeout.Token);
        Assert.False(backend.Pending());
        releaseAdmission.TrySetResult();
        using var connection = await backend.AcceptTcpClientAsync(timeout.Token);
        await SpinUntilAsync(() => Volatile.Read(ref started) == 1, timeout.Token);
        Assert.Equal(1, started);

        var bytes = new byte[7];
        await connection.GetStream().ReadExactlyAsync(bytes, timeout.Token);
        Assert.Equal("request", Encoding.UTF8.GetString(bytes));
        Assert.Equal(0, await connection.GetStream().ReadAsync(new byte[1], timeout.Token));
        await connection.GetStream().WriteAsync(Encoding.UTF8.GetBytes("response"), timeout.Token);
        var reply = await PrivateAccessFrames.ReceiveAsync(stream, timeout.Token);
        Assert.Equal(PrivateAccessFrameKind.Data, reply.Kind);
        Assert.Equal("response", Encoding.UTF8.GetString(reply.Payload.Span));

        revoked = true;
        await PrivateAccessFrames.SendAsync(connector, PrivateAccessFrameKind.LeaseChallenge,
            RandomNumberGenerator.GetBytes(32), timeout.Token);
        await close.Task.WaitAsync(timeout.Token);
        await SpinUntilAsync(() => kube.CountDeleted == 4, timeout.Token);
        Assert.Null(app.Services.GetRequiredService<PrivateAccessConnectorRegistry>().Find("target-a"));
        Assert.Equal(4, kube.CountDeleted);
        Assert.True(kube.HasWorkerBaseline);
        Assert.All(kube.Deletions, deletion =>
        {
            Assert.Equal("session-a", deletion.Session);
            Assert.Equal("tenant-a", deletion.Tenant);
            Assert.Equal("target-a", deletion.Target);
            Assert.Equal("uid-owned", deletion.PreconditionUid);
            Assert.Equal("1", deletion.PreconditionRevision);
        });
        Assert.True(workerRun.IsCompleted || worker.State is WebSocketState.Aborted or WebSocketState.Closed);
        await app.StopAsync(timeout.Token);
    }

    private static PrivateAccessLeaseSnapshot Lease() => new(
        "tenant-a", "user-a", "grant-a", "session-a", "postgresql", "resource-a",
        "deployment-a", "endpoint-a", "target-a", "cell-a", "scope-a", "runtime-a",
        "database", NetworkPort.Create(5432), NetworkPort.Create(5432), 1,
        DateTime.UtcNow.AddMinutes(1), DateTime.UtcNow.AddMinutes(2));

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

    private static async Task ChallengeAsync(WebSocket socket, CancellationToken ct)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        await PrivateAccessFrames.SendAsync(socket, PrivateAccessFrameKind.LeaseChallenge, nonce, ct);
        var accepted = await PrivateAccessFrames.ReceiveAsync(socket, ct);
        Assert.Equal(PrivateAccessFrameKind.LeaseAccepted, accepted.Kind);
        Assert.Equal(nonce, accepted.Payload.ToArray());
    }

    private static async Task SpinUntilAsync(Func<bool> predicate, CancellationToken ct)
    {
        while (!predicate())
            await Task.Delay(20, ct);
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

    private sealed class FixedKubernetesProvider(IKubernetes client) : IKubernetesClientProvider
    {
        public Task<IKubernetes> GetAsync(string id, CancellationToken ct = default)
        {
            Assert.Equal("target-a", id);
            return Task.FromResult(client);
        }

        public Task<IKubernetes> GetDefaultAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("A target-specific Kubernetes client is required.");
    }

    private sealed record Deletion(
        string Session,
        string Tenant,
        string Target,
        string PreconditionUid,
        string PreconditionRevision);

    private sealed class RecordingKubernetes : DelegatingHandler
    {
        private readonly ConcurrentDictionary<string, string> objects = new();
        private readonly ConcurrentQueue<string> created = new();
        private readonly ConcurrentQueue<Deletion> deleted = new();
        public readonly TaskCompletionSource<string> Ticket = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<Deletion> Deletions => deleted.ToArray();
        public int CountDeleted => deleted.Count;
        public int CountCreated(string kind) => created.Count(path => path.Contains(kind, StringComparison.Ordinal));
        public bool HasWorkerBaseline => objects.Keys.Any(path =>
            path.EndsWith($"/{PrivateAccessWorkerManifest.WorkerBaselineName}", StringComparison.Ordinal));

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
                var name = metadata["name"]!.GetValue<string>();
                var key = path.EndsWith("/namespaces", StringComparison.Ordinal)
                    ? $"{path}/{name}"
                    : $"{path}/{name}";
                if (!objects.TryAdd(key, body.ToJsonString()))
                    return Response(request, HttpStatusCode.Conflict, "{}");
                created.Enqueue(key);
                if (path.EndsWith("/secrets", StringComparison.Ordinal))
                    Ticket.TrySetResult(body["stringData"]!["ticket"]!.GetValue<string>());
                return Response(request, HttpStatusCode.Created, body.ToJsonString());
            }
            if (request.Method == HttpMethod.Get)
                return objects.TryGetValue(path, out var value)
                    ? Response(request, HttpStatusCode.OK, value)
                    : Response(request, HttpStatusCode.NotFound, "{}");
            if (request.Method == HttpMethod.Delete)
            {
                if (!objects.TryRemove(path, out var value))
                    return Response(request, HttpStatusCode.NotFound, "{}");
                var labels = JsonNode.Parse(value)!["metadata"]!["labels"]!;
                var options = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
                deleted.Enqueue(new Deletion(
                    labels[PrivateAccessWorkerManifest.SessionLabel]!.GetValue<string>(),
                    labels[PrivateAccessWorkerManifest.TenantLabel]!.GetValue<string>(),
                    labels[PrivateAccessWorkerManifest.TargetLabel]!.GetValue<string>(),
                    options["preconditions"]!["uid"]!.GetValue<string>(),
                    options["preconditions"]!["resourceVersion"]!.GetValue<string>()));
                return Response(request, HttpStatusCode.OK, "{}");
            }
            throw new InvalidOperationException($"Unexpected Kubernetes operation {request.Method} {path}");
        }

        private static HttpResponseMessage Response(HttpRequestMessage request, HttpStatusCode code, string body) =>
            new(code)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
    }
}
