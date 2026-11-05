using MongoDB.Driver;
using Pole.Features.ContainerRegistries.Application.Mongo;
using Pole.Features.ContainerRegistries.Domain;

namespace Pole.Features.ContainerRegistries.Application.Credentials;

public delegate Task<RegistryCredential?> LoadRegistryCredential(
    string tenantId,
    string registryProjectId,
    RegistryCredentialPurpose purpose,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadRegistryCredential(IMongoCollection<RegistryCredential> registryCredentials)
{
    public async Task<RegistryCredential?> InvokeAsync(
        string tenantId,
        string registryProjectId,
        RegistryCredentialPurpose purpose,
        CancellationToken cancellationToken = default) =>
        await registryCredentials
            .Find(ContainerRegistryFilters.CredentialByTenantProjectAndPurpose(
                tenantId,
                registryProjectId,
                purpose))
            .FirstOrDefaultAsync(cancellationToken);
}
