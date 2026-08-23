using MongoDB.Driver;
using Pole.Features.Quotas.Domain;

namespace Pole.Features.Quotas.Application.Persistence;

public delegate Task<TenantQuotaUsage?> LoadTenantQuotaUsage(
    string tenantId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadTenantQuotaUsage(
    IMongoCollection<TenantQuotaUsage> usages)
{
    public async Task<TenantQuotaUsage?> InvokeAsync(
        string tenantId,
        CancellationToken cancellationToken = default) =>
        await usages
            .Find(QuotaUsageFilters.ByTenant(tenantId))
            .FirstOrDefaultAsync(cancellationToken);
}
