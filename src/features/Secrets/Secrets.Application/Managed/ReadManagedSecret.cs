using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Pole.Encryption;
using Pole.Features.Secrets.Application.Persistence;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Managed;

internal sealed class EnvelopeReadManagedSecret(
    LoadManagedSecret loadSecret,
    LoadSecretVersion loadVersion,
    IEnvelopeEncryptionService encryption)
{
    public async Task<ReadManagedSecretOutcome> InvokeAsync(
        string tenantId,
        ManagedSecretOwner owner,
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
        if (result is not byte[] decrypted)
            return new ManagedSecretEncryptionUnavailable();
        try
        {
            var values = JsonSerializer.Deserialize<ImmutableDictionary<string, string>>(decrypted)!;
            return new ManagedSecretValues(secret.Id, secret.Name,
                ((ProviderManagedSecret)secret.Management).KubernetesType == KubernetesSecretType.BasicAuthentication ? ManagedSecretKubernetesType.BasicAuthentication : ManagedSecretKubernetesType.Opaque,
                secret.CurrentVersion, values);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decrypted);
        }
    }
}
