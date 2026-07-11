using MongoDB.Driver;
using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.ContainerRegistries.Domain;
using Pole.ResultType;

namespace Pole.Features.ContainerRegistries.Application.Security;

public sealed record ResolvedRegistryCredential(
    string Username,
    string Secret,
    DateTime? ExpiresAtUtc);

public interface IRegistryCredentialResolver
{
    Task<Result<ResolvedRegistryCredential>> ResolveAsync(
        string tenantId,
        string registryProjectId,
        RegistryCredentialPurpose purpose,
        CancellationToken cancellationToken = default);
}

public sealed class RegistryCredentialResolver(
    IContainerRegistryDbContext dbContext,
    IRegistryCredentialProtector protector,
    TimeProvider timeProvider) : IRegistryCredentialResolver
{
    public async Task<Result<ResolvedRegistryCredential>> ResolveAsync(
        string tenantId,
        string registryProjectId,
        RegistryCredentialPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        var credential = await dbContext.RegistryCredentials
            .Find(item =>
                item.TenantId == tenantId &&
                item.RegistryProjectId == registryProjectId &&
                item.Purpose == purpose)
            .FirstOrDefaultAsync(cancellationToken);
        if (credential is null)
            return Failures.NotFound("The container registry credential was not found.");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (credential.ExpiresAtUtc is not null && credential.ExpiresAtUtc <= now)
            return Failures.Conflict("The container registry credential has expired and must be rotated.");

        var secret = protector.Unprotect(credential.ProtectedSecret);
        if (!secret.TryGetValue(out var value, out var failure))
            return failure;

        return new ResolvedRegistryCredential(credential.Username, value, credential.ExpiresAtUtc);
    }
}
