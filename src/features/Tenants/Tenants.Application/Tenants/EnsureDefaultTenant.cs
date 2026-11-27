// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Application.Mongo;
using Pole.Features.Tenants.Domain;
using Pole.ResultType;

namespace Pole.Features.Tenants.Application.Tenants;

public delegate Task EnsureDefaultTenant(Tenant tenant, CancellationToken cancellationToken = default);

internal sealed class MongoEnsureDefaultTenant(
    IMongoCollection<Tenant> _tenants,
    StoreOutboxWrites storeOutboxWrites,
    RunMongoOutcomeTransaction<Unit> runTransaction
)
{
    public Task InvokeAsync(Tenant tenant, CancellationToken cancellationToken = default) => runTransaction(
        async (transaction, ct) =>
        {
            var result = await _tenants.UpdateOneAsync(
                transaction.Session,
                TenantFilters.ById(tenant.Id),
                TenantUpdates.CreateIfMissing(tenant),
                new UpdateOptions { IsUpsert = true },
                ct);
            if (result.MatchedCount != 0)
                return transaction.Abort(Unit.Value);
            await storeOutboxWrites(
                transaction.Session,
                tenant.Members
                    .Select(member => member.UserId)
                    .Distinct(StringComparer.Ordinal)
                    .Select(userId => TenantAccessEventExtensions.MembershipChanged(tenant.Id, userId))
                    .ToArray(),
                ct);
            return transaction.Commit(Unit.Value);
        },
        cancellationToken);
}
