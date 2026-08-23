using MongoDB.Driver;
using Pole.Features.Quotas.Domain;

namespace Pole.Features.Quotas.Application.Persistence;

internal static class QuotaPolicyFilters
{
    public static FilterDefinition<TenantQuotaPolicy> ByTenant(string tenantId) =>
        Builders<TenantQuotaPolicy>.Filter.Eq(policy => policy.TenantId, tenantId);
}
