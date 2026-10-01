using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Pole.Features.DeploymentTargets.Application.Connections;

internal sealed record ValidatedConnectorCertificate(
    string FingerprintSha256,
    string PublicKeySpkiSha256,
    DateTime NotAfterUtc);

internal static class ConnectorCertificateValidation
{
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";
    private const int MaximumDerBytes = 16 * 1024;

    public static ValidatedConnectorCertificate? Validate(
        string certificateDerBase64,
        DateTime nowUtc)
    {
        byte[] der;
        try
        {
            der = Convert.FromBase64String(certificateDerBase64);
        }
        catch (FormatException)
        {
            return null;
        }

        try
        {
            if (der.Length is < 1 or > MaximumDerBytes ||
                X509Certificate2.GetCertContentType(der) != X509ContentType.Cert)
                return null;

            using var certificate = X509CertificateLoader.LoadCertificate(der);
            if (!string.Equals(certificate.Subject, certificate.Issuer, StringComparison.Ordinal) ||
                certificate.NotBefore.ToUniversalTime() > nowUtc ||
                certificate.NotAfter.ToUniversalTime() <= nowUtc.AddHours(1) ||
                certificate.NotAfter.ToUniversalTime() > nowUtc.AddDays(90))
                return null;

            var basicConstraints = certificate.Extensions
                .OfType<X509BasicConstraintsExtension>()
                .SingleOrDefault();
            var keyUsage = certificate.Extensions
                .OfType<X509KeyUsageExtension>()
                .SingleOrDefault();
            var enhancedUsage = certificate.Extensions
                .OfType<X509EnhancedKeyUsageExtension>()
                .SingleOrDefault();
            if (basicConstraints is null || basicConstraints.CertificateAuthority ||
                keyUsage is null ||
                (keyUsage.KeyUsages & X509KeyUsageFlags.DigitalSignature) == 0 ||
                enhancedUsage is null ||
                !enhancedUsage.EnhancedKeyUsages.Cast<Oid>()
                    .Any(oid => oid.Value == ClientAuthenticationOid))
                return null;

            byte[] spki;
            using (var rsa = certificate.GetRSAPublicKey())
            using (var ecdsa = certificate.GetECDsaPublicKey())
            {
                spki = rsa is { KeySize: >= 3072 }
                    ? rsa.ExportSubjectPublicKeyInfo()
                    : ecdsa is { KeySize: >= 256 }
                        ? ecdsa.ExportSubjectPublicKeyInfo()
                        : [];
            }
            if (spki.Length == 0)
                return null;

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(certificate);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationTime = nowUtc;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ClientAuthenticationOid));
            if (!chain.Build(certificate))
                return null;

            return new ValidatedConnectorCertificate(
                Convert.ToHexString(SHA256.HashData(certificate.RawData)),
                Convert.ToHexString(SHA256.HashData(spki)),
                certificate.NotAfter.ToUniversalTime());
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(der);
        }
    }
}
