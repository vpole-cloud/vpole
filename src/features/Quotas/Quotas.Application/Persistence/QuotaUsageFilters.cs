using MongoDB.Driver;
using Pole.Features.Quotas.Domain;

namespace Pole.Features.Quotas.Application.Persistence;

internal static class QuotaUsageFilters
{
    public static FilterDefinition<TenantQuotaUsage> ByTenant(string tenantId) =>
        Builders<TenantQuotaUsage>.Filter.Eq(usage => usage.TenantId, tenantId);
}
