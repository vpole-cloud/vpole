using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Domain;
using Pole.ResultType;

namespace Pole.Features.Tenants.Application.Persistence.Tenants;

public delegate Task EnsureDefaultTenant(
    Tenant tenant,
    CancellationToken cancellationToken = default);

internal sealed class MongoEnsureDefaultTenant(
    IMongoDatabase database,
    StoreOutboxWrites storeOutboxWrites,
    RunMongoOutcomeTransaction<Unit> runTransaction)
{
    private readonly IMongoCollection<Tenant> _tenants = database.GetCollection<Tenant>("Tenants");

    public Task InvokeAsync(
        Tenant tenant,
        CancellationToken cancellationToken = default) => runTransaction(async (transaction, ct) =>
    {
        var result = await _tenants.UpdateOneAsync(
            transaction.Session,
            TenantFilters.ById(tenant.Id),
            TenantUpdates.CreateIfMissing(tenant),
            new UpdateOptions { IsUpsert = true },
            ct);
        if (result.UpsertedId is null) return transaction.Abort(Unit.Value);
        await storeOutboxWrites(transaction.Session,
            tenant.Members.Select(member => member.UserId).Distinct(StringComparer.Ordinal)
                .Select(userId => TenantAccessEventExtensions.MembershipChanged(tenant.Id, userId)).ToArray(), ct);
        return transaction.Commit(Unit.Value);
    }, cancellationToken);
}
