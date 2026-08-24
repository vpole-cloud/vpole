using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.ContainerRegistries.Domain;
using Pole.ResultType;

namespace Pole.Features.ContainerRegistries.Application.Security;

public delegate ValueTask<Result<string, RegistryCredentialUnavailable>>
    UnprotectRegistryCredentialSecret(
        RegistryCredential credential,
        CancellationToken cancellationToken = default);

public sealed class EnvelopeUnprotectRegistryCredentialSecret(Pole.Encryption.IEnvelopeEncryptionService encryption)
{
    public async ValueTask<Result<string, RegistryCredentialUnavailable>> InvokeAsync(
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
            ? new Ok<string>(secret)
            : new Err<RegistryCredentialUnavailable>(new(credential.RegistryProjectId));
    }
}
