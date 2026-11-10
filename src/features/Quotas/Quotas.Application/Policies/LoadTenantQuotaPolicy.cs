using MongoDB.Driver;
using Pole.Features.Quotas.Application.Mongo;
using Pole.Features.Quotas.Domain;

namespace Pole.Features.Quotas.Application.Policies;

public delegate Task<TenantQuotaPolicy?> LoadTenantQuotaPolicy(
    string tenantId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadTenantQuotaPolicy(
    IMongoCollection<TenantQuotaPolicy> policies
)
{
    public async Task<TenantQuotaPolicy?> InvokeAsync(string tenantId, CancellationToken cancellationToken = default) =>
        await policies.Find(QuotaPolicyFilters.ByTenant(tenantId)).FirstOrDefaultAsync(cancellationToken);
}
