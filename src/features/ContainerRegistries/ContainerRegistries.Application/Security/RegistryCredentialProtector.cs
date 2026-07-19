using Pole.Encryption;
using Pole.Features.ContainerRegistries.Domain;
using Pole.ResultType;

namespace Pole.Features.ContainerRegistries.Application.Security;

public interface IRegistryCredentialProtector
{
    ValueTask<Result<EncryptedValue>> ProtectAsync(
        string tenantId,
        string credentialId,
        long secretVersion,
        string secret,
        CancellationToken cancellationToken = default);

    ValueTask<Result<string>> UnprotectAsync(
        RegistryCredential credential,
        CancellationToken cancellationToken = default);
}

public sealed class RegistryCredentialProtector(IEnvelopeEncryptionService envelopeEncryption)
    : IRegistryCredentialProtector
{
    public ValueTask<Result<EncryptedValue>> ProtectAsync(
        string tenantId,
        string credentialId,
        long secretVersion,
        string secret,
        CancellationToken cancellationToken = default) =>
        envelopeEncryption.EncryptStringAsync(
            secret,
            RegistryCredentialEncryptionContext.Create(tenantId, credentialId, secretVersion),
            cancellationToken);

    public ValueTask<Result<string>> UnprotectAsync(
        RegistryCredential credential,
        CancellationToken cancellationToken = default) =>
        envelopeEncryption.DecryptStringAsync(
            credential.EncryptedSecret,
            RegistryCredentialEncryptionContext.Create(
                credential.TenantId,
                credential.Id,
                credential.SecretVersion),
            cancellationToken);
}

internal static class RegistryCredentialEncryptionContext
{
    public static EnvelopeEncryptionContext Create(
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
