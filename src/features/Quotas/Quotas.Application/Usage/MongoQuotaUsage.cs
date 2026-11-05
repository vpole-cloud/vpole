using Pole.Features.Quotas.Application.Mongo;
using Pole.Features.Quotas.Application;
using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Features.Quotas.Domain;

namespace Pole.Features.Quotas.Application.Usage;

internal static class MongoQuotaUsage
{
    public static Task<TenantQuotaUsage> TouchAsync(
        IMongoCollection<TenantQuotaUsage> usages,
        IClientSessionHandle session,
        string tenantId,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        usages.FindOneAndUpdateAsync(
            session,
            QuotaUsageFilters.ByTenant(tenantId),
            Builders<TenantQuotaUsage>.Update
                .SetOnInsert(usage => usage.Values, ImmutableList<QuotaValue>.Empty)
                .Inc(usage => usage.AdmissionRevision, 1)
                .Set(usage => usage.UpdatedAtUtc, nowUtc),
            new FindOneAndUpdateOptions<TenantQuotaUsage, TenantQuotaUsage>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After
            },
            cancellationToken);
}
