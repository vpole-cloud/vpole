using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Pole.PrivateAccess.Prototype;
using Pole.PrivateAccess.Prototype.Tcp;
using Xunit;

namespace PrivateAccess.Tests;

public sealed class BrokerTests
{
    [Fact]
    public async Task Relays_bidirectional_data_and_preserves_half_close()
    {
        await using var fixture = new Fixture();
        await using var channel = await fixture.OpenAsync();
        await channel.WriteAsync("hello"u8.ToArray(), fixture.Token);
        Assert.Equal("hello", await ReadAsync(channel, 5, fixture.Token));
        await channel.CompleteWritesAsync(fixture.Token);
        Assert.Equal("bye", await ReadAsync(channel, 3, fixture.Token));
        Assert.Equal(0, await channel.ReadAsync(new byte[1], fixture.Token));
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("subject")]
    [InlineData("deployment")]
    [InlineData("endpoint")]
    [InlineData("grant")]
    public async Task Admission_is_bound_to_every_identity_component(string changed)
    {
        await using var fixture = new Fixture();
        var request = fixture.Binding.Request;
        request = changed switch
        {
            "deployment" => request with { ResourceDeploymentId = "foreign" },
            "endpoint" => request with { EndpointId = "foreign" },
            "grant" => request with { GrantId = "foreign" },
            _ => request
        };
        if (changed == "tenant")
            fixture.Authority.Binding = fixture.Binding with { Identity = fixture.Binding.Identity with { TenantId = "foreign" } };
        if (changed == "subject")
            fixture.Authority.Binding = fixture.Binding with { Identity = fixture.Binding.Identity with { SubjectId = "foreign" } };
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.OpenAsync(request));
        Assert.Equal(0, fixture.BackendConnections);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("expiry")]
    [InlineData("unavailable")]
    [InlineData("revision")]
    [InlineData("hang")]
    public async Task Authorization_loss_closes_active_stream_and_denies_new_admission(string reason)
    {
        await using var fixture = new Fixture();
        await using var channel = await fixture.OpenAsync();
        await channel.WriteAsync("ping"u8.ToArray(), fixture.Token);
        Assert.Equal("ping", await ReadAsync(channel, 4, fixture.Token));
        switch (reason)
        {
            case "revoke": fixture.Authority.Enabled = false; break;
            case "expiry": fixture.Authority.ExpiresAt = DateTimeOffset.UtcNow; break;
            case "unavailable": fixture.Authority.Unavailable = true; break;
            case "revision": fixture.Authority.Binding = fixture.Binding with { Revision = 2 }; break;
            case "hang": fixture.Authority.Hang = true; break;
        }
        await fixture.BackendClosed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (reason != "revision")
            await Assert.ThrowsAnyAsync<Exception>(() => fixture.OpenAsync());
    }

    [Fact]
    public async Task A_connector_cannot_claim_another_target()
    {
        await using var fixture = new Fixture(connectorTarget: "foreign");
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.OpenAsync());
        Assert.Equal(0, fixture.BackendConnections);
    }

    [Fact]
    public async Task Reconnect_gets_a_fresh_authorization()
    {
        await using var fixture = new Fixture();
        await using (var first = await fixture.OpenAsync())
        {
            await first.WriteAsync("a"u8.ToArray(), fixture.Token);
            Assert.Equal("a", await ReadAsync(first, 1, fixture.Token));
        }
        await fixture.BackendClosed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.Authority.Enabled = false;
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.OpenAsync());
        Assert.Equal(1, fixture.BackendConnections);
    }

    [Fact]
    public async Task Malformed_request_with_a_destination_is_rejected_without_poisoning_the_broker()
    {
        await using var fixture = new Fixture();
        await fixture.SendDestinationInjectionAsync();
        Assert.Equal(0, fixture.BackendConnections);
        await using var admitted = await fixture.OpenAsync();
        await admitted.WriteAsync("ok"u8.ToArray(), fixture.Token);
        Assert.Equal("ok", await ReadAsync(admitted, 2, fixture.Token));
    }

    [Fact]
    public async Task Reconnected_connector_can_serve_a_new_authorized_session()
    {
        await using var fixture = new Fixture();
        await using (var first = await fixture.OpenAsync())
        {
            await first.WriteAsync("a"u8.ToArray(), fixture.Token);
            Assert.Equal("a", await ReadAsync(first, 1, fixture.Token));
        }
        await fixture.BackendClosed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await using var second = await fixture.OpenAsync();
        await second.WriteAsync("b"u8.ToArray(), fixture.Token);
        Assert.Equal("b", await ReadAsync(second, 1, fixture.Token));
        Assert.Equal(2, fixture.BackendConnections);
    }

    private static async Task<string> ReadAsync(PrivateAccessChannel channel, int length, CancellationToken ct)
    {
        var bytes = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var count = await channel.ReadAsync(bytes.AsMemory(offset), ct);
            Assert.True(count > 0, "Unexpected end of stream.");
            offset += count;
        }
        return Encoding.UTF8.GetString(bytes);
    }

    private sealed class MutableAuthority(AccessBinding binding) : IPrivateAccessAuthority
    {
        public volatile AccessBinding Binding = binding;
        public volatile bool Enabled = true;
        public volatile bool Unavailable;
        public volatile bool Hang;
        public TaskCompletionSource ReleaseHang { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1);

        public async ValueTask<AuthorizeAccessOutcome> AuthorizeAsync(AccessIdentity identity, AccessRequest request, CancellationToken ct)
        {
            if (Hang)
                await ReleaseHang.Task; // Deliberately ignores cancellation: the lease must still expire.
            if (Unavailable)
                return new AuthorityUnavailable();
            if (!Enabled || ExpiresAt <= DateTimeOffset.UtcNow || identity != Binding.Identity || request != Binding.Request)
                return new AccessDenied();
            return new AccessAllowed(Binding, ExpiresAt);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(20));
        private readonly X509Certificate2 server = Certificate("server");
        private readonly X509Certificate2 connector = Certificate("connector");
        private readonly X509Certificate2 user = Certificate("user");
        private readonly TcpListener backend = new(IPAddress.Loopback, 0);
        private readonly Task brokerTask;
        private readonly Task connectorTask;
        private readonly Task backendTask;
        private readonly TcpBroker broker;
        private int connections;

        public AccessBinding Binding { get; } = new(new AccessIdentity("tenant-a", "subject-a"),
            new AccessRequest("grant-a", "deployment-a", "redis"), "target-a", 1);
        public MutableAuthority Authority { get; }
        public CancellationToken Token => lifetime.Token;
        public int BackendConnections => Volatile.Read(ref connections);
        public TaskCompletionSource BackendClosed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Fixture(string connectorTarget = "target-a")
        {
            Authority = new MutableAuthority(Binding);
            backend.Start();
            backendTask = BackendAsync();
            var peers = new Dictionary<string, PeerIdentity>
            {
                [TlsIdentity.Pin(connector)] = new ConnectorPeer("target-a"),
                [TlsIdentity.Pin(user)] = new UserPeer(Binding.Identity)
            };
            broker = new TcpBroker(new IPEndPoint(IPAddress.Loopback, 0), server, peers, Authority, TimeSpan.FromMilliseconds(900));
            brokerTask = broker.RunAsync(Token);
            var endpoints = new Dictionary<EndpointKey, TcpDestination>
            {
                [new EndpointKey("tenant-a", "deployment-a", "redis")] = new("127.0.0.1", ((IPEndPoint)backend.LocalEndpoint).Port)
            };
            connectorTask = new TargetConnector("127.0.0.1", broker.Port, connector, TlsIdentity.Pin(server),
                connectorTarget, endpoints, TimeSpan.FromMilliseconds(900)).RunAsync(Token);
        }

        public Task<PrivateAccessChannel> OpenAsync(AccessRequest? request = null) =>
            new LocalForward("127.0.0.1", broker.Port, user, TlsIdentity.Pin(server), request ?? Binding.Request).OpenAsync(Token);

        public async Task SendDestinationInjectionAsync()
        {
            await using var wire = await TlsIdentity.ConnectAsync("127.0.0.1", broker.Port, user, TlsIdentity.Pin(server), Token);
            var payload = Encoding.UTF8.GetBytes("""{"GrantId":"grant-a","ResourceDeploymentId":"deployment-a","EndpointId":"redis","Host":"attacker"}""");
            await wire.SendAsync(FrameKind.Client, payload, Token);
            await Assert.ThrowsAnyAsync<IOException>(() => wire.ReceiveAsync(Token).AsTask());
        }

        private async Task BackendAsync()
        {
            try
            {
                while (!Token.IsCancellationRequested)
                {
                    using var socket = await backend.AcceptTcpClientAsync(Token);
                    Interlocked.Increment(ref connections);
                    try
                    {
                        var buffer = new byte[1024];
                        int count;
                        while ((count = await socket.GetStream().ReadAsync(buffer, Token)) > 0)
                            await socket.GetStream().WriteAsync(buffer.AsMemory(0, count), Token);
                        await socket.GetStream().WriteAsync("bye"u8.ToArray(), Token);
                    }
                    catch (IOException) { }
                    finally { BackendClosed.TrySetResult(); }
                }
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync();
            Authority.ReleaseHang.TrySetResult();
            await Task.WhenAll(brokerTask, connectorTask, backendTask);
            backend.Stop();
            server.Dispose();
            connector.Dispose();
            user.Dispose();
            lifetime.Dispose();
        }
    }

    internal static X509Certificate2 Certificate(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
    }
}
