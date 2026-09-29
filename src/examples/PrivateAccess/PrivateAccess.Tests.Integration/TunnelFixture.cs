using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Pole.PrivateAccess.Prototype.Tcp;

namespace Pole.PrivateAccess.Prototype.Tests.Integration;

internal sealed class TunnelFixture : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new(TimeSpan.FromMinutes(3));
    private CancellationTokenSource brokerLifetime;
    private CancellationTokenSource connectorLifetime;
    private readonly X509Certificate2 server = Certificate("broker");
    private readonly X509Certificate2 connector = Certificate("connector");
    private readonly X509Certificate2 user = Certificate("user");
    private readonly IReadOnlyDictionary<string, PeerIdentity> peers;
    private readonly IReadOnlyDictionary<EndpointKey, TcpDestination> endpoints;
    private readonly LocalForward forward;
    private TcpBroker broker;
    private Task brokerTask;
    private Task[] connectorTasks;
    private readonly Task forwardTask;
    public FixtureAuthority Authority { get; }
    public int Port => forward.ListeningPort ?? throw new InvalidOperationException("Forward is not listening.");
    public CancellationToken Token => lifetime.Token;
    public static TimeSpan LeaseDuration => TimeSpan.FromSeconds(2);

    public TunnelFixture(string host, int port)
    {
        var binding = new AccessBinding(new AccessIdentity("tenant-a", "subject-a"),
            new AccessRequest("grant-a", "deployment-a", "database"), "target-a", 1);
        Authority = new FixtureAuthority(binding);
        peers = new Dictionary<string, PeerIdentity>
        {
            [TlsIdentity.Pin(connector)] = new ConnectorPeer("target-a"),
            [TlsIdentity.Pin(user)] = new UserPeer(binding.Identity)
        };
        endpoints = new Dictionary<EndpointKey, TcpDestination>
        {
            [new EndpointKey("tenant-a", "deployment-a", "database")] = new(host, port)
        };
        brokerLifetime = CancellationTokenSource.CreateLinkedTokenSource(Token);
        connectorLifetime = CancellationTokenSource.CreateLinkedTokenSource(Token);
        broker = CreateBroker(0);
        brokerTask = broker.RunAsync(brokerLifetime.Token);
        connectorTasks = StartConnectors();
        forward = new LocalForward("127.0.0.1", broker.Port, user, TlsIdentity.Pin(server), binding.Request);
        forwardTask = forward.RunAsync(0, Token);
    }

    public async Task RestartBrokerAsync()
    {
        var port = broker.Port;
        await brokerLifetime.CancelAsync();
        await brokerTask;
        brokerLifetime.Dispose();
        brokerLifetime = CancellationTokenSource.CreateLinkedTokenSource(Token);
        broker = CreateBroker(port);
        brokerTask = broker.RunAsync(brokerLifetime.Token);
    }

    public async Task RestartConnectorsAsync()
    {
        await connectorLifetime.CancelAsync();
        await Task.WhenAll(connectorTasks);
        connectorLifetime.Dispose();
        connectorLifetime = CancellationTokenSource.CreateLinkedTokenSource(Token);
        connectorTasks = StartConnectors();
    }

    private TcpBroker CreateBroker(int port) => new(new IPEndPoint(IPAddress.Loopback, port), server, peers, Authority, LeaseDuration);

    private Task[] StartConnectors() => Enumerable.Range(0, 8).Select(_ =>
        new TargetConnector("127.0.0.1", broker.Port, connector, TlsIdentity.Pin(server), "target-a", endpoints, LeaseDuration)
            .RunAsync(connectorLifetime.Token)).ToArray();

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        try { await Task.WhenAll(connectorTasks.Append(brokerTask).Append(forwardTask)); }
        finally
        {
            brokerLifetime.Dispose();
            connectorLifetime.Dispose();
            lifetime.Dispose();
            server.Dispose();
            connector.Dispose();
            user.Dispose();
        }
    }

    private static X509Certificate2 Certificate(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
    }
}

internal sealed class FixtureAuthority(AccessBinding binding) : IPrivateAccessAuthority
{
    private AccessAllowed allowed = new(binding, DateTimeOffset.UtcNow.AddMinutes(3));
    public volatile bool Enabled = true;
    public void Expire() => Interlocked.Exchange(ref allowed, new AccessAllowed(binding, DateTimeOffset.UtcNow));

    public ValueTask<AuthorizeAccessOutcome> AuthorizeAsync(AccessIdentity identity, AccessRequest request, CancellationToken ct)
    {
        var current = Volatile.Read(ref allowed);
        AuthorizeAccessOutcome outcome = Enabled && current.ExpiresAt > DateTimeOffset.UtcNow &&
            current.Binding.Identity == identity && current.Binding.Request == request
            ? current : new AccessDenied();
        return ValueTask.FromResult(outcome);
    }
}
