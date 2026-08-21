using MongoDB.Driver;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Tenants.Application.Common;

internal static class TenantFilters
{
    public static FilterDefinition<Tenant> ById(string tenantId) =>
        Builders<Tenant>.Filter.Eq(tenant => tenant.Id, tenantId);

    public static FilterDefinition<Tenant> ByIds(
        IReadOnlyCollection<string> tenantIds) =>
        Builders<Tenant>.Filter.In(tenant => tenant.Id, tenantIds);
}
