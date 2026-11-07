using MongoDB.Driver;
using Pole.Features.FeatureName.Domain;

namespace Pole.Features.FeatureName.Application.Mongo;

internal static class ResourceNameFilters
{
    internal static FilterDefinition<ResourceName> ById(string tenantId, string id) =>
        Builders<ResourceName>.Filter.Eq(resource => resource.TenantId, tenantId) &
        Builders<ResourceName>.Filter.Eq(resource => resource.Id, id);

    internal static FilterDefinition<ResourceName> AtRevision(string tenantId, string id, long revision) =>
        ById(tenantId, id) & Builders<ResourceName>.Filter.Eq(resource => resource.Revision, revision);
}
