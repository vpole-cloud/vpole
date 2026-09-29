using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Pole.PrivateAccess.Prototype;
using Pole.PrivateAccess.Prototype.Tcp;
using Xunit;

namespace PrivateAccess.Tests;

public sealed class PartitionTests
{
    [Fact]
    public async Task Connector_closes_backend_when_renewals_disappear_while_data_keeps_flowing()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        using var serverCertificate = BrokerTests.Certificate("broker");
        using var connectorCertificate = BrokerTests.Certificate("connector");
        using var backend = new TcpListener(IPAddress.Loopback, 0);
        using var broker = new TcpListener(IPAddress.Loopback, 0);
        backend.Start();
        broker.Start();
        var backendClosed = EchoUntilClosedAsync();
        var exchanges = 0;
        var brokerSession = ServePartitionAsync();
        var endpoints = new Dictionary<EndpointKey, TcpDestination>
        {
            [new EndpointKey("tenant-a", "deployment-a", "redis")] = new("127.0.0.1", ((IPEndPoint)backend.LocalEndpoint).Port)
        };
        var connector = new TargetConnector("127.0.0.1", ((IPEndPoint)broker.LocalEndpoint).Port,
            connectorCertificate, TlsIdentity.Pin(serverCertificate), "target-a", endpoints, TimeSpan.FromMilliseconds(600));
        var connectorTask = connector.RunAsync(ct);
        try
        {
            await backendClosed.WaitAsync(TimeSpan.FromSeconds(3));
            await brokerSession.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(exchanges > 1, "The data path must remain active while renewal responses are withheld.");
        }
        finally
        {
            await deadline.CancelAsync();
            await connectorTask;
        }

        async Task EchoUntilClosedAsync()
        {
            using var socket = await backend.AcceptTcpClientAsync(ct);
            var stream = socket.GetStream();
            var buffer = new byte[1024];
            int count;
            while ((count = await stream.ReadAsync(buffer, ct)) > 0)
                await stream.WriteAsync(buffer.AsMemory(0, count), ct);
        }

        async Task ServePartitionAsync()
        {
            using var socket = await broker.AcceptTcpClientAsync(ct);
            using var tls = new SslStream(socket.GetStream(), false, (_, remote, _, _) =>
                TlsIdentity.Matches(remote, TlsIdentity.Pin(connectorCertificate)));
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = serverCertificate,
                ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls13
            }, ct);
            await using var wire = new Wire(tls);
            Assert.Equal(FrameKind.Connector, (await wire.ReceiveAsync(ct)).Kind);
            await wire.SendJsonAsync(FrameKind.Open, new AccessBinding(new AccessIdentity("tenant-a", "member-a"),
                new AccessRequest("grant-a", "deployment-a", "redis"), "target-a", 1), ct);
            var first = Wire.Decode<LeaseChallenge>(await wire.ReceiveAsync(ct));
            await wire.SendJsonAsync(FrameKind.Lease, new LeaseReply(first.Nonce, 600), ct);
            try
            {
                while (true)
                {
                    var frame = await wire.ReceiveAsync(ct);
                    if (frame.Kind is FrameKind.Ready or FrameKind.Data)
                    {
                        exchanges++;
                        await Task.Delay(10, ct);
                        await wire.SendAsync(FrameKind.Data, "ping"u8.ToArray(), ct);
                    }
                    // Deliberately ignore all further challenges while leaving TLS and data operational.
                }
            }
            catch (IOException) { }
        }
    }
}
