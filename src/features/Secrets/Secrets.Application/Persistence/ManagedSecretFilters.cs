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
        Builders<Secret>.Filter.Eq(x => ((ProviderManagedSecret)x.Management).ResourceType,
            ResourceTypeId.Create(owner.ResourceType).Value) &
        Builders<Secret>.Filter.Eq(x => ((ProviderManagedSecret)x.Management).ResourceId,
            SecretIdentity.ObjectId(owner.ResourceId));

    public static FilterDefinition<Secret> Adoption(ManagedSecretAdoptionSnapshot request) =>
        Owner(request.Request.TenantId, request.Request.Owner) &
        SecretFilters.Identity(request.Request.TenantId, request.SecretId) &
        Builders<Secret>.Filter.Eq(item => item.ResourceGroupId, request.Request.ResourceGroupId) &
        Builders<Secret>.Filter.Eq(item => item.IsolationScopeId, request.Request.IsolationScopeId) &
        Builders<Secret>.Filter.Eq(item => item.Revision, request.Revision) &
        Builders<Secret>.Filter.Eq(item => item.ReferenceFence, request.ReferenceFence) &
        Builders<Secret>.Filter.Eq(item => item.CurrentVersion, request.ValueVersion);
}
