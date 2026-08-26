using Pole.Features.ContainerRegistries.Application.Persistence;
using Pole.Features.ContainerRegistries.Application.Persistence.Credentials;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.ContainerRegistries.Domain;
using Pole.ResultType;

namespace Pole.Features.ContainerRegistries.Application.Security;

public sealed record ResolvedRegistryCredential(
    string Username,
    string Secret,
    DateTime? ExpiresAtUtc)
{
    public override string ToString() => "ResolvedRegistryCredential([REDACTED])";
}

public delegate Task<Result<ResolvedRegistryCredential, RegistryCredentialUnavailable>>
    ResolveRegistryCredential(
        string tenantId,
        string registryProjectId,
        RegistryCredentialPurpose purpose,
        CancellationToken cancellationToken = default);

public sealed class RegistryCredentialResolver(
    LoadRegistryCredential loadRegistryCredential,
    UnprotectRegistryCredentialSecret unprotectSecret,
    TimeProvider timeProvider)
{
    public async Task<Result<ResolvedRegistryCredential, RegistryCredentialUnavailable>> InvokeAsync(
        string tenantId,
        string registryProjectId,
        RegistryCredentialPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        var credential = await loadRegistryCredential(tenantId, registryProjectId, purpose, cancellationToken);
        if (credential is null ||
            credential.ExpiresAtUtc is { } expiresAt && expiresAt <= timeProvider.GetUtcNow().UtcDateTime)
        {
            return new Err<RegistryCredentialUnavailable>(new(registryProjectId));
        }

        var secret = await unprotectSecret(credential, cancellationToken);
        return secret switch
        {
            Ok<string> value => new Ok<ResolvedRegistryCredential>(new(
                credential.Username,
                value.Value,
                credential.ExpiresAtUtc)),
            Err<RegistryCredentialUnavailable> error => new Err<RegistryCredentialUnavailable>(error.Value)
        };
    }
}
