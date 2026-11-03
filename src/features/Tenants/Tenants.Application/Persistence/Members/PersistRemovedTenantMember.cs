using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Tenants.Application.Persistence.Members;

public sealed record RemoveTenantMember(
    string TenantId,
    string MemberId);

public sealed record TenantMemberRemoved(string MemberId);

public union PersistRemovedTenantMemberOutcome(
    TenantMemberRemoved,
    TenantNotFound,
    TenantMemberNotFound,
    TenantLastOwner);

public delegate Task<PersistRemovedTenantMemberOutcome> PersistRemovedTenantMember(
    RemoveTenantMember request,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistRemovedTenantMember(
    IMongoCollection<Tenant> _tenants,
    StoreOutboxWrites storeOutboxWrites,
    RunMongoOutcomeTransaction<PersistRemovedTenantMemberOutcome> runTransaction)
{
    public Task<PersistRemovedTenantMemberOutcome> InvokeAsync(
        RemoveTenantMember request,
        CancellationToken cancellationToken = default) => runTransaction(async (transaction, ct) =>
        {
            var tenant = await _tenants
                .Find(transaction.Session, TenantFilters.ById(request.TenantId))
                .FirstOrDefaultAsync(ct);
            if (tenant is null)
            {
                return transaction.Abort(new TenantNotFound(request.TenantId));
            }

            var existingMember = tenant.Members.FirstOrDefault(member => member.Id == request.MemberId);
            if (existingMember is null)
            {
                return transaction.Abort(new TenantMemberNotFound(request.TenantId, request.MemberId));
            }

            if (existingMember.Role == TenantRole.Owner &&
                tenant.Members.Count(member => member.Role == TenantRole.Owner) == 1)
            {
                return transaction.Abort(new TenantLastOwner(request.TenantId, request.MemberId));
            }

            var result = await _tenants.UpdateOneAsync(
                transaction.Session,
                TenantFilters.ByIdWithMemberAndOwnerCanBeRemoved(
                    request.TenantId,
                    request.MemberId),
                TenantUpdates.RemoveMember(request.MemberId),
                cancellationToken: ct);
            if (result.ModifiedCount != 0)
            {
                await storeOutboxWrites(transaction.Session,
                    [TenantAccessEventExtensions.MembershipChanged(request.TenantId, existingMember.UserId)],
                    ct);
                return transaction.Commit(new TenantMemberRemoved(request.MemberId));
            }

            var failure = await TenantMemberMutationFailures.ResolveAsync(
                _tenants,
                transaction.Session,
                request.TenantId,
                request.MemberId,
                ct);
            PersistRemovedTenantMemberOutcome outcome = failure switch
            {
                TenantNotFound error => error,
                TenantMemberNotFound error => error,
                TenantLastOwner error => error
            };
            return transaction.Abort(outcome);
        },
        cancellationToken);
}
