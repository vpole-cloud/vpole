using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.PrivateAccess.Primitives;
using Pole.Features.PrivateAccess.Runtime;
using Xunit;

namespace Pole.Features.PrivateAccess.Tests;

public sealed class PrivateAccessConnectorTlsTests
{
    [Fact]
    public async Task LiveMutualTls_UsesExactPin_NotCertificateSubject_AndRechecksRevocation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var serverCertificate = Certificate("localhost", server: true);
        using var connectorCertificate = Certificate("target-a", server: false);
        using var impostorCertificate = Certificate("target-a", server: false);
        var fingerprint = Convert.ToHexString(SHA256.HashData(connectorCertificate.RawData));
        var revoked = false;
        var port = FreePort();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PrivateAccess:Enabled"] = "true",
            ["PrivateAccess:ConnectorPort"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["PrivateAccess:WorkerImage"] = "test/worker:1",
            ["PrivateAccess:BrokerUri"] = "https://broker.example",
            ["PrivateAccess:BrokerAddresses:0"] = "192.0.2.8",
            ["Kestrel:Endpoints:PrivateAccessConnector:Url"] = $"https://127.0.0.1:{port}"
        });
        builder.WebHost.ConfigureKestrel(options => options.ConfigureHttpsDefaults(https =>
            https.ServerCertificate = serverCertificate));
        builder.ConfigurePrivateAccessListener();
        builder.Services.AddPrivateAccessRuntime(builder.Configuration);
        builder.Services.AddSingleton<ResolveDeploymentTargetConnector>((pin, _) =>
            Task.FromResult<ResolveDeploymentTargetConnectorOutcome>(pin == fingerprint && !revoked
                ? new DeploymentTargetConnectorIdentity("target-a", "connector-a", 1, 1)
                : new DeploymentTargetConnectorRejected()));
        builder.Services.AddSingleton<RecordDeploymentTargetConnectorContact>((_, _) =>
            Task.FromResult<RecordDeploymentTargetConnectorContactOutcome>(new DeploymentTargetConnectorContactRecorded()));
        await using var app = builder.Build();
        app.UsePrivateAccessRuntime();
        app.MapPrivateAccessConnectorEndpoint();
        await app.StartAsync(timeout.Token);
        var uri = new Uri($"wss://127.0.0.1:{port}/private-access/connectors");
        using var impostor = new ClientWebSocket();
        await Assert.ThrowsAsync<WebSocketException>(() => ConnectAsync(
            impostor, uri, impostorCertificate, serverCertificate, timeout.Token));
        using var client = new ClientWebSocket();
        await ConnectAsync(client, uri, connectorCertificate, serverCertificate, timeout.Token);
        var nonce = RandomNumberGenerator.GetBytes(32);
        await PrivateAccessFrames.SendAsync(client, PrivateAccessFrameKind.LeaseChallenge, nonce, timeout.Token);
        var accepted = await PrivateAccessFrames.ReceiveAsync(client, timeout.Token);
        Assert.Equal(PrivateAccessFrameKind.LeaseAccepted, accepted.Kind);
        Assert.Equal(nonce, accepted.Payload.ToArray());
        var registry = app.Services.GetRequiredService<PrivateAccessConnectorRegistry>();
        Assert.NotNull(registry.Find("target-a"));
        revoked = true;
        await PrivateAccessFrames.SendAsync(client, PrivateAccessFrameKind.LeaseChallenge,
            RandomNumberGenerator.GetBytes(32), timeout.Token);
        var failure = await Record.ExceptionAsync(async () =>
            await PrivateAccessFrames.ReceiveAsync(client, timeout.Token));
        Assert.True(failure is WebSocketException or IOException, failure?.GetType().Name);
        Assert.Null(registry.Find("target-a"));
        await app.StopAsync(timeout.Token);
    }

    private static async Task ConnectAsync(
        ClientWebSocket socket,
        Uri uri,
        X509Certificate2 client,
        X509Certificate2 server,
        CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        handler.SslOptions.ClientCertificates = new X509CertificateCollection { client };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is not null && CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(certificate.GetRawCertData()), SHA256.HashData(server.RawData));
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        await socket.ConnectAsync(uri, invoker, cancellationToken);
        // The upgraded stream must remain usable after its handshake invoker is
        // disposed, as in the CLI's per-connection no-redirect transport.
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
