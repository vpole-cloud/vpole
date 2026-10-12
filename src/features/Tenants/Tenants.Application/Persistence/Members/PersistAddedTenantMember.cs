using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Tenants.Application.Persistence.Members;

public union PersistAddedTenantMemberOutcome(
    TenantMember,
    TenantNotFound,
    TenantMemberAlreadyExists);

public delegate Task<PersistAddedTenantMemberOutcome> PersistAddedTenantMember(
    TenantMember member,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistAddedTenantMember(
    IMongoDatabase database,
    StoreOutboxWrites storeOutboxWrites,
    RunMongoOutcomeTransaction<PersistAddedTenantMemberOutcome> runTransaction)
{
    private readonly IMongoCollection<Tenant> _tenants = database.GetCollection<Tenant>("Tenants");

    public Task<PersistAddedTenantMemberOutcome> InvokeAsync(
        TenantMember member,
        CancellationToken cancellationToken = default) => runTransaction(async (transaction, ct) =>
    {
        var updatedTenant = await _tenants.FindOneAndUpdateAsync(
            transaction.Session,
            TenantFilters.ByIdWithoutMemberUser(member.TenantId, member.UserId),
            TenantUpdates.AddMember(member),
            new FindOneAndUpdateOptions<Tenant> { ReturnDocument = ReturnDocument.After },
            ct);
        if (updatedTenant is not null)
        {
            await storeOutboxWrites(transaction.Session, [TenantAccessEventExtensions.MembershipChanged(member.TenantId, member.UserId)], ct);
            return transaction.Commit(member);
        }

        var tenant = await _tenants
            .Find(transaction.Session, TenantFilters.ById(member.TenantId))
            .FirstOrDefaultAsync(ct);
        PersistAddedTenantMemberOutcome failure = tenant is null
            ? new TenantNotFound(member.TenantId)
            : new TenantMemberAlreadyExists(member.TenantId, member.UserId);
        return transaction.Abort(failure);
    }, cancellationToken);
}
