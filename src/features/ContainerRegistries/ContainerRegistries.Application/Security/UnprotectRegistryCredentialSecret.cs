using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.ContainerRegistries.Domain;
using Pole.ResultType;

namespace Pole.Features.ContainerRegistries.Application.Security;

public union UnprotectRegistryCredentialOutcome(
    string,
    RegistryCredentialUnavailable);

public delegate ValueTask<UnprotectRegistryCredentialOutcome>
    UnprotectRegistryCredentialSecret(
        RegistryCredential credential,
        CancellationToken cancellationToken = default);

public sealed class EnvelopeUnprotectRegistryCredentialSecret(Pole.Encryption.IEnvelopeEncryptionService encryption)
{
    public async ValueTask<UnprotectRegistryCredentialOutcome> InvokeAsync(
        RegistryCredential credential,
        CancellationToken cancellationToken = default)
    {
        var result = await encryption.DecryptStringAsync(
            credential.EncryptedSecret,
            RegistryCredentialEncryptionContext.Create(
                credential.TenantId,
                credential.Id,
                credential.SecretVersion),
            cancellationToken);
        return result.TryGetValue(out var secret, out _)
            ? secret
            : new RegistryCredentialUnavailable(credential.RegistryProjectId);
    }
}
