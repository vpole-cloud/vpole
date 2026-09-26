using System.Security.Cryptography;
using System.Text.Json;
using Pole.Encryption;
using Pole.Features.Secrets.Application.Persistence;
using Pole.Features.Secrets.Contracts;
using Pole.ResultType;

namespace Pole.Features.Secrets.Application.Managed;

internal sealed class EnvelopeReadManagedSecretKey(
    LoadManagedSecret loadSecret,
    LoadSecretVersion loadVersion,
    IEnvelopeEncryptionService encryption)
{
    public async Task<ReadManagedSecretKeyOutcome> InvokeAsync(
        string tenantId,
        ManagedSecretOwner owner,
        string key,
        CancellationToken ct)
    {
        var secret = await loadSecret(tenantId, owner, ct);
        if (secret is null)
            return new ManagedSecretUnavailable();
        var version = await loadVersion(secret.TenantId, secret.Id, secret.CurrentVersion, ct);
        if (version is null)
            return new ManagedSecretUnavailable();
        var result = await encryption.DecryptAsync(version.Value,
            EnvelopeEncryptionContext.ForTenant(secret.TenantId, "Secret", secret.Id, "values", version.Version), ct);
        if (result is not Ok<byte[]> decrypted)
            return new ManagedSecretEncryptionUnavailable();
        try
        {
            using var document = JsonDocument.Parse(decrypted.Value);
            return document.RootElement.TryGetProperty(key, out var value)
                ? new ManagedSecretKey(value.GetString()!)
                : new ManagedSecretUnavailable();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decrypted.Value);
        }
    }
}
