using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Domains.Domain;
using Pole.Workloads;

namespace Pole.Features.Domains.Application.Persistence;

internal static class DomainFilters
{
    public static FilterDefinition<TenantDomainZone> ZoneForTenant(string tenantId) =>
        Builders<TenantDomainZone>.Filter.ForTenant(tenantId);

    public static FilterDefinition<HostnameBinding> BindingForDeployment(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string resourceDeploymentId) =>
        Builders<HostnameBinding>.Filter.And(
            Builders<HostnameBinding>.Filter.ForTenant(tenantId),
            Builders<HostnameBinding>.Filter.Eq(item => item.ResourceType, resourceType.Value),
            Builders<HostnameBinding>.Filter.Eq(item => item.ResourceId, resourceId),
            Builders<HostnameBinding>.Filter.Eq(item => item.ResourceDeploymentId, resourceDeploymentId));
}
