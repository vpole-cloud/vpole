// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.Quotas.Application.Mongo;
using Pole.Features.Quotas.Domain;

namespace Pole.Features.Quotas.Application.Usage;

public delegate Task ApplyQuotaUsageDelta(
    string tenantId,
    QuotaDelta delta,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

internal sealed class MongoApplyQuotaUsageDelta(
    IMongoCollection<TenantQuotaUsage> usages,
    TimeProvider timeProvider
)
{
    public async Task InvokeAsync(
        string tenantId,
        QuotaDelta delta,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Quota usage adjustment requires an active transaction.");
        if (delta.IsEmpty)
            return;
        if (delta.Values.Any(value => value.Value >= 0))
            throw new ArgumentException("Usage-release values must be less than zero.", nameof(delta));

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var usage = await MongoQuotaUsage.TouchAsync(usages, session, tenantId, nowUtc, cancellationToken);
        var values = QuotaValues.AddAndClampToZero(usage.Values, delta.Values);
        await usages.UpdateOneAsync(
            session,
            QuotaUsageFilters.ByTenant(tenantId),
            Builders<TenantQuotaUsage>.Update
                .Set(candidate => candidate.Values, values)
                .Set(candidate => candidate.UpdatedAtUtc, nowUtc),
            cancellationToken: cancellationToken);
    }
}
