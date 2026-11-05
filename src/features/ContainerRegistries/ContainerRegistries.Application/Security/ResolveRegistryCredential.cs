using Pole.Features.ContainerRegistries.Application.Mongo;
using Pole.Features.ContainerRegistries.Application.Credentials;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.ContainerRegistries.Domain;

namespace Pole.Features.ContainerRegistries.Application.Security;

public sealed record ResolvedRegistryCredential(
    string Username,
    string Secret,
    DateTime? ExpiresAtUtc)
{
    public override string ToString() => "ResolvedRegistryCredential([REDACTED])";
}

public union ResolveRegistryCredentialOutcome(
    ResolvedRegistryCredential,
    RegistryCredentialUnavailable);

public delegate Task<ResolveRegistryCredentialOutcome>
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
    public async Task<ResolveRegistryCredentialOutcome> InvokeAsync(
        string tenantId,
        string registryProjectId,
        RegistryCredentialPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        var credential = await loadRegistryCredential(tenantId, registryProjectId, purpose, cancellationToken);
        if (credential is null ||
            credential.ExpiresAtUtc is { } expiresAt && expiresAt <= timeProvider.GetUtcNow().UtcDateTime)
        {
            return new RegistryCredentialUnavailable(registryProjectId);
        }

        var secret = await unprotectSecret(credential, cancellationToken);
        return secret switch
        {
            string value => new ResolvedRegistryCredential(
                credential.Username,
                value,
                credential.ExpiresAtUtc),
            RegistryCredentialUnavailable error => error
        };
    }
}
