using MongoDB.Driver;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Workloads;

namespace Pole.Features.Secrets.Application.Persistence;

internal static class ManagedSecretFilters
{
    public static FilterDefinition<Secret> Owner(string tenantId, ManagedSecretOwner owner) =>
        Builders<Secret>.Filter.Eq(x => x.TenantId, SecretIdentity.ObjectId(tenantId)) &
        Builders<Secret>.Filter.OfType<SecretManagement, ProviderManagedSecret>(x => x.Management) &
        Builders<Secret>.Filter.Eq(x => ((ProviderManagedSecret)x.Management).ResourceType, ResourceTypeId.Create(owner.ResourceType).Value) &
        Builders<Secret>.Filter.Eq(x => ((ProviderManagedSecret)x.Management).ResourceId, SecretIdentity.ObjectId(owner.ResourceId));
}
