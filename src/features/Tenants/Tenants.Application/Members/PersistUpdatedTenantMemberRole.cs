using Pole.Features.Tenants.Application.Tenants;
using Pole.Features.Tenants.Application.Mongo;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Tenants.Application.Members;

public sealed record UpdateTenantMemberRole(
    string TenantId,
    string MemberId,
    TenantRole Role);

public union PersistUpdatedTenantMemberRoleOutcome(
    TenantMember,
    TenantNotFound,
    TenantMemberNotFound,
    TenantLastOwner);

public delegate Task<PersistUpdatedTenantMemberRoleOutcome>
    PersistUpdatedTenantMemberRole(
        UpdateTenantMemberRole request,
        CancellationToken cancellationToken = default);

internal sealed class MongoPersistUpdatedTenantMemberRole(
    IMongoCollection<Tenant> _tenants,
    StoreOutboxWrites storeOutboxWrites,
    RunMongoOutcomeTransaction<PersistUpdatedTenantMemberRoleOutcome> runTransaction)
{
    public Task<PersistUpdatedTenantMemberRoleOutcome> InvokeAsync(
        UpdateTenantMemberRole request,
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

            if (existingMember.Role == request.Role)
                return transaction.Abort(existingMember);

            var removesOwner = request.Role != TenantRole.Owner;
            if (existingMember.Role == TenantRole.Owner &&
                removesOwner &&
                tenant.Members.Count(member => member.Role == TenantRole.Owner) == 1)
            {
                return transaction.Abort(new TenantLastOwner(request.TenantId, request.MemberId));
            }

            var updatedTenant = await _tenants.FindOneAndUpdateAsync(
                transaction.Session,
                removesOwner
                    ? TenantFilters.ByIdWithMemberAndOwnerCanBeRemoved(
                        request.TenantId,
                        request.MemberId)
                    : TenantFilters.ByIdWithMember(request.TenantId, request.MemberId),
                TenantUpdates.SetMemberRole(request.Role),
                new FindOneAndUpdateOptions<Tenant> { ReturnDocument = ReturnDocument.After },
                ct);
            var updatedMember = updatedTenant?.Members.FirstOrDefault(member => member.Id == request.MemberId);
            if (updatedMember is not null)
            {
                await storeOutboxWrites(transaction.Session,
                    [TenantAccessEventExtensions.MembershipChanged(request.TenantId, updatedMember.UserId)],
                    ct);
                return transaction.Commit(updatedMember);
            }

            var failure = await TenantMemberMutationFailures.ResolveAsync(
                _tenants,
                transaction.Session,
                request.TenantId,
                request.MemberId,
                ct);
            PersistUpdatedTenantMemberRoleOutcome outcome = failure switch
            {
                TenantNotFound error => error,
                TenantMemberNotFound error => error,
                TenantLastOwner error => error
            };
            return transaction.Abort(outcome);
        },
        cancellationToken);
}
