// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Pole.Features.PrivateAccess.Runtime;

public sealed class PrivateAccessInternalIdentity : IDisposable
{
    private readonly byte[] pin;

    public PrivateAccessInternalIdentity(
        string certificatePath,
        string certificatePassword,
        string certificateSha256Pin)
    {
        if (certificateSha256Pin.Length != 64 || !certificateSha256Pin.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid Private Access peer certificate pin.");
        pin = Convert.FromHexString(certificateSha256Pin);
        Certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, certificatePassword);
        if (!Matches(Certificate) || !Certificate.HasPrivateKey)
            throw new InvalidOperationException("Private Access peer certificate does not match its configured pin.");
    }

    public X509Certificate2 Certificate { get; }

    public void Dispose() => Certificate.Dispose();

    public bool Matches(X509Certificate? certificate) => certificate is not null &&
        certificate.GetCertHash(HashAlgorithmName.SHA256) is { } actual &&
        CryptographicOperations.FixedTimeEquals(actual, pin) && certificate is X509Certificate2 x509 &&
        x509.NotBefore.ToUniversalTime() <= DateTime.UtcNow && x509.NotAfter.ToUniversalTime() > DateTime.UtcNow;
}
