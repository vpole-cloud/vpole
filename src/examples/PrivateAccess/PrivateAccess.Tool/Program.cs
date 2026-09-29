using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Pole.PrivateAccess.Prototype;
using Pole.PrivateAccess.Prototype.Tcp;

if (args.Length != 2 || args[0] is not ("init" or "broker" or "connector" or "forward"))
{
    Console.Error.WriteLine("Usage: PrivateAccess.Tool init|broker|connector|forward <fixture-directory>");
    return 2;
}

var directory = Path.GetFullPath(args[1]);
var json = FixtureJson.Options;
if (args[0] == "init")
{
    if (Directory.Exists(directory))
        throw new InvalidOperationException("Choose a new fixture directory; existing credentials are never overwritten.");
    Directory.CreateDirectory(directory);
    if (!OperatingSystem.IsWindows())
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    foreach (var name in new[] { "broker", "connector", "user" })
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllBytes(Path.Combine(directory, $"{name}.pfx"), generated.Export(X509ContentType.Pfx));
        File.WriteAllBytes(Path.Combine(directory, $"{name}.cer"), generated.Export(X509ContentType.Cert));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Path.Combine(directory, $"{name}.pfx"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    var fixture = new Fixture("127.0.0.1", 14443, 16379, "target-a", "127.0.0.1", 6379,
        new AccessBinding(new AccessIdentity("tenant-a", "member-a"), new AccessRequest("grant-a", "deployment-a", "redis"), "target-a", 1),
        DateTimeOffset.UtcNow.AddHours(1), true);
    File.WriteAllText(Path.Combine(directory, "fixture.json"), JsonSerializer.Serialize(fixture, json));
    Console.WriteLine("Created short-lived local fixture credentials and fixture.json. Configure the destination before starting roles.");
    return 0;
}

var path = Path.Combine(directory, "fixture.json");
var settings = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(path), json)
    ?? throw new InvalidDataException("Missing fixture configuration.");
if (settings.BrokerPort is < 1 or > 65535 || settings.LocalPort is < 1 or > 65535 || settings.DestinationPort is < 1 or > 65535)
    throw new InvalidDataException("Ports must be between 1 and 65535.");
using var brokerCertificate = Load("broker");
using var connectorCertificate = Load("connector");
using var userCertificate = Load("user");
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
var leaseDuration = TimeSpan.FromSeconds(3);
var brokerPin = TlsIdentity.Pin(brokerCertificate);
try
{
    switch (args[0])
    {
        case "broker":
            var peers = new Dictionary<string, PeerIdentity>
            {
                [TlsIdentity.Pin(connectorCertificate)] = new ConnectorPeer(settings.TargetId),
                [TlsIdentity.Pin(userCertificate)] = new UserPeer(settings.Binding.Identity)
            };
            var broker = new TcpBroker(new IPEndPoint(IPAddress.Parse(settings.BrokerHost), settings.BrokerPort),
                brokerCertificate, peers, new FileAuthority(path), leaseDuration);
            Console.WriteLine("Broker started. Each admission and renewal reads the fixture authority.");
            await broker.RunAsync(cancellation.Token);
            break;
        case "connector":
            var endpoints = new Dictionary<EndpointKey, TcpDestination>
            {
                [new EndpointKey(settings.Binding.Identity.TenantId, settings.Binding.Request.ResourceDeploymentId, settings.Binding.Request.EndpointId)] =
                    new TcpDestination(settings.DestinationHost, settings.DestinationPort)
            };
            Console.WriteLine("Outbound connector started (one active session per worker; four workers).");
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => new TargetConnector(settings.BrokerHost, settings.BrokerPort,
                connectorCertificate, brokerPin, settings.TargetId, endpoints, leaseDuration).RunAsync(cancellation.Token)));
            break;
        case "forward":
            Console.WriteLine($"Local forward listening on 127.0.0.1:{settings.LocalPort}.");
            await new LocalForward(settings.BrokerHost, settings.BrokerPort, userCertificate, brokerPin, settings.Binding.Request)
                .RunAsync(settings.LocalPort, cancellation.Token);
            break;
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
return 0;

X509Certificate2 Load(string name) => name == (args[0] == "forward" ? "user" : args[0])
    ? X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(directory, $"{name}.pfx"), null)
    : X509CertificateLoader.LoadCertificateFromFile(Path.Combine(directory, $"{name}.cer"));

internal sealed record Fixture(
    string BrokerHost,
    int BrokerPort,
    int LocalPort,
    string TargetId,
    string DestinationHost,
    int DestinationPort,
    AccessBinding Binding,
    DateTimeOffset ExpiresAt,
    bool Enabled);

internal sealed class FileAuthority(string path) : IPrivateAccessAuthority
{
    public async ValueTask<AuthorizeAccessOutcome> AuthorizeAsync(AccessIdentity identity, AccessRequest request, CancellationToken ct)
    {
        Fixture? state;
        try
        {
            await using var stream = File.OpenRead(path);
            state = await JsonSerializer.DeserializeAsync<Fixture>(stream, FixtureJson.Options, ct);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AuthorityUnavailable();
        }
        if (state is null || !state.Enabled || state.ExpiresAt <= DateTimeOffset.UtcNow)
            return new AccessDenied();
        if (state.Binding.Identity != identity || state.Binding.Request != request)
            return new AccessDenied();
        return new AccessAllowed(state.Binding, state.ExpiresAt);
    }
}

internal static class FixtureJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };
}
