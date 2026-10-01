using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Pole.Connector;

public static class ConnectorEnrollment
{
    public static async Task EnrollAsync(
        BrokerOrigin origin,
        string targetId,
        string tokenFile,
        string stateDirectory,
        CancellationToken cancellationToken)
    {
        if (targetId is not { Length: 24 } || !targetId.All(Uri.IsHexDigit))
            throw new ArgumentException("A valid target ID is required.", nameof(targetId));

        var token = await ReadEnrollmentTokenAsync(tokenFile, cancellationToken);
        var store = ConnectorIdentityStore.Open(stateDirectory);
        using var certificate = store.HasPending
            ? store.LoadPending()
            : await CreateAndStageAsync(store, cancellationToken);

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var endpoint = origin.ApiPath(
            $"/api/v1/platform/deployment-targets/{targetId}/connectors/enroll");
        using var response = await client.PostAsJsonAsync(endpoint, new EnrollmentBody(
            token,
            Convert.ToBase64String(certificate.Export(X509ContentType.Cert))),
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Connector enrollment was rejected.");

        var enrolled = await response.Content.ReadFromJsonAsync<EnrolledIdentity>(
            cancellationToken: cancellationToken);
        if (enrolled is null || enrolled.TargetId != targetId ||
            enrolled.ConnectorId is not { Length: 24 } ||
            enrolled.Generation < 1 || enrolled.Revision < 1)
            throw new InvalidDataException("Connector enrollment returned invalid identity metadata.");

        store.PromotePending();
        Console.WriteLine("Connector enrollment completed.");
    }

    private static async Task<X509Certificate2> CreateAndStageAsync(
        ConnectorIdentityStore store,
        CancellationToken cancellationToken)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            "CN=Pole connector",
            key,
            HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature,
            true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") },
            true));
        var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(30));
        try
        {
            await store.WritePendingAsync(certificate, cancellationToken);
            return certificate;
        }
        catch
        {
            certificate.Dispose();
            throw;
        }
    }

    private static async Task<string> ReadEnrollmentTokenAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is < 64 or > 1024)
            throw new IOException("Enrollment token file is unavailable.");
        var value = (await File.ReadAllTextAsync(path, cancellationToken)).Trim();
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new InvalidDataException("Enrollment token has an invalid format.");
        return value;
    }

    private sealed record EnrollmentBody(
        string Token,
        string CertificateDerBase64)
    {
        public override string ToString() => "EnrollmentBody(Token=[REDACTED], Certificate=[REDACTED])";
    }

    private sealed record EnrolledIdentity(
        string TargetId,
        string ConnectorId,
        long Generation,
        long Revision);
}
