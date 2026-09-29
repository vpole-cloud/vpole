using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Pole.PrivateAccess.Prototype.Tcp;

public closed record PeerIdentity;
public sealed record UserPeer(AccessIdentity Identity) : PeerIdentity;
public sealed record ConnectorPeer(string TargetId) : PeerIdentity;

/// <summary>Explicit certificate pins are a prototype fixture, not production enrollment or user OIDC.</summary>
public static class TlsIdentity
{
    public static string Pin(X509Certificate certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    public static bool Matches(X509Certificate? certificate, string pin)
    {
        if (certificate is null || Pin(certificate) != pin)
            return false;
        using var parsed = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        var now = DateTime.UtcNow;
        return parsed.NotBefore.ToUniversalTime() <= now && parsed.NotAfter.ToUniversalTime() > now;
    }

    internal static async Task<Wire> ConnectAsync(
        string host,
        int port,
        X509Certificate2 certificate,
        string serverPin,
        CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, ct);
            var stream = new SslStream(client.GetStream(), false, (_, remote, _, _) => Matches(remote, serverPin));
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                ClientCertificates = new X509CertificateCollection { certificate },
                EnabledSslProtocols = SslProtocols.Tls13
            }, ct);
            return new Wire(stream);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
