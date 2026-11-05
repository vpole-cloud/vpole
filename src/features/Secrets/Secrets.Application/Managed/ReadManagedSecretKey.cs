using System.Security.Cryptography;
using System.Text.Json;
using Pole.Encryption;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application.Bootstrap;
using Pole.Features.Secrets.Application.Declarations;
using Pole.Features.Secrets.Application.Lifecycle;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Application.References;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Contracts;

namespace Pole.Features.Secrets.Application.Managed;

internal sealed class EnvelopeReadManagedSecretKey(
    LoadManagedSecret loadSecret,
    LoadSecretVersion loadVersion,
    IEnvelopeEncryptionService encryption)
{
    public async Task<ReadManagedSecretKeyOutcome> InvokeAsync(
        ReadManagedSecretKeyRequest request,
        CancellationToken ct)
    {
        var secret = await loadSecret(request.TenantId, request.Owner, ct);
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
            using var document = JsonDocument.Parse(decrypted);
            return document.RootElement.TryGetProperty(request.Key, out var value)
                ? new ManagedSecretKey(value.GetString()!)
                : new ManagedSecretUnavailable();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decrypted);
        }
    }
}
