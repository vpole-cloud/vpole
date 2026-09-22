using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Pole.Encryption;
using Pole.Features.Secrets.Application.Persistence;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.ResultType;

namespace Pole.Features.Secrets.Application.Managed;

internal sealed class EnvelopeReadManagedSecret(
    LoadManagedSecret loadSecret,
    LoadSecretVersion loadVersion,
    IEnvelopeEncryptionService encryption)
{
    public async Task<Result<ManagedSecretValues, ManagedSecretError>> InvokeAsync(
        string tenantId,
        ManagedSecretOwner owner,
        CancellationToken ct)
    {
        var secret = await loadSecret(tenantId, owner, ct);
        if (secret is null)
            return new Err<ManagedSecretError>(new ManagedSecretUnavailable());
        var version = await loadVersion(secret.TenantId, secret.Id, secret.CurrentVersion, ct);
        if (version is null)
            return new Err<ManagedSecretError>(new ManagedSecretUnavailable());
        var result = await encryption.DecryptAsync(version.Value,
            EnvelopeEncryptionContext.ForTenant(secret.TenantId, "Secret", secret.Id, "values", version.Version), ct);
        if (result is not Ok<byte[]> decrypted)
            return new Err<ManagedSecretError>(new ManagedSecretEncryptionUnavailable());
        try
        {
            var values = JsonSerializer.Deserialize<ImmutableDictionary<string, string>>(decrypted.Value)!;
            return new Ok<ManagedSecretValues>(new(secret.Id, secret.Name,
                ((ProviderManagedSecret)secret.Management).ProjectionType == SecretProjectionType.BasicAuthentication ? ManagedSecretProjectionType.BasicAuthentication : ManagedSecretProjectionType.Opaque,
                secret.CurrentVersion, values));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decrypted.Value);
        }
    }
}
