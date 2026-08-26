using MongoDB.Driver;
using Pole.Features.Quotas.Domain;

namespace Pole.Features.Quotas.Application.Persistence.Policies;

public delegate Task<TenantQuotaPolicy> PersistTenantQuotaPolicy(
    string tenantId,
    QuotaDelta limits,
    DateTime updatedAtUtc,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistTenantQuotaPolicy(
    IMongoCollection<TenantQuotaPolicy> policies)
{
    public async Task<TenantQuotaPolicy> InvokeAsync(
        string tenantId,
        QuotaDelta limits,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var existing = await policies
            .Find(QuotaPolicyFilters.ByTenant(tenantId))
            .FirstOrDefaultAsync(cancellationToken);
        var policy = QuotaDomainFactory.CreatePolicy(
            tenantId,
            limits,
            updatedAtUtc,
            existing?.CreatedAtUtc);
        await policies.ReplaceOneAsync(
            QuotaPolicyFilters.ByTenant(tenantId),
            policy,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
        return policy;
    }
}
