using System.Security.Cryptography;
using System.Text.Json;
using Pole.Encryption;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application;

public union EncryptSecretVersionOutcome(
    SecretVersion,
    SecretEncryptionFailed);

public delegate Task<EncryptSecretVersionOutcome> EncryptSecretVersion(
    Secret secret,
    Dictionary<string, string> values,
    CancellationToken ct);

internal sealed class EnvelopeEncryptSecretVersion(
    IEnvelopeEncryptionService encryption,
    TimeProvider clock)
{
    public async Task<EncryptSecretVersionOutcome> InvokeAsync(
        Secret secret,
        Dictionary<string, string> values,
        CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(values);
        try
        {
            var result = await encryption.EncryptAsync(
                bytes,
                EnvelopeEncryptionContext.ForTenant(
                    secret.TenantId,
                    "Secret",
                    secret.Id,
                    "values",
                    secret.CurrentVersion),
                ct);
            return result switch
            {
                EncryptedValue encrypted => new SecretVersion(
                    $"{secret.Id}:{secret.CurrentVersion}",
                    secret.TenantId,
                    secret.Id,
                    secret.CurrentVersion,
                    encrypted,
                    clock.GetUtcNow().UtcDateTime),
                EnvelopeEncryptionFailed => new SecretEncryptionFailed()
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
