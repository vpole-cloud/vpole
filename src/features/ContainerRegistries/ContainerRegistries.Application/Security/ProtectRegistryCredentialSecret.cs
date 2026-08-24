using Pole.Encryption;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.ResultType;

namespace Pole.Features.ContainerRegistries.Application.Security;

public delegate ValueTask<Result<EncryptedValue, RegistryCredentialProtectionFailed>>
    ProtectRegistryCredentialSecret(
        string tenantId,
        string credentialId,
        long secretVersion,
        string secret,
        CancellationToken cancellationToken = default);

public sealed class EnvelopeProtectRegistryCredentialSecret(IEnvelopeEncryptionService encryption)
{
    public async ValueTask<Result<EncryptedValue, RegistryCredentialProtectionFailed>> InvokeAsync(
        string tenantId,
        string credentialId,
        long secretVersion,
        string secret,
        CancellationToken cancellationToken = default)
    {
        var result = await encryption.EncryptStringAsync(
            secret,
            RegistryCredentialEncryptionContext.Create(tenantId, credentialId, secretVersion),
            cancellationToken);
        return result.TryGetValue(out var encrypted, out _)
            ? new Ok<EncryptedValue>(encrypted)
            : new Err<RegistryCredentialProtectionFailed>(new());
    }
}

internal static class RegistryCredentialEncryptionContext
{
    internal static EnvelopeEncryptionContext Create(
        string tenantId,
        string credentialId,
        long secretVersion) =>
        EnvelopeEncryptionContext.ForTenant(
            tenantId,
            "container-registry-credential",
            credentialId,
            "secret",
            secretVersion);
}
