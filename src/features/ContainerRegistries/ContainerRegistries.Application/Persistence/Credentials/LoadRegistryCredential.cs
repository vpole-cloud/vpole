using MongoDB.Driver;
using Pole.Features.ContainerRegistries.Domain;

namespace Pole.Features.ContainerRegistries.Application.Persistence.Credentials;

public delegate Task<RegistryCredential?> LoadRegistryCredential(
    string tenantId,
    string registryProjectId,
    RegistryCredentialPurpose purpose,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadRegistryCredential(IMongoDatabase database)
{
    public async Task<RegistryCredential?> InvokeAsync(
        string tenantId,
        string registryProjectId,
        RegistryCredentialPurpose purpose,
        CancellationToken cancellationToken = default) =>
        await database.GetCollection<RegistryCredential>("RegistryCredentials")
            .Find(ContainerRegistryFilters.CredentialByTenantProjectAndPurpose(
                tenantId,
                registryProjectId,
                purpose))
            .FirstOrDefaultAsync(cancellationToken);
}
