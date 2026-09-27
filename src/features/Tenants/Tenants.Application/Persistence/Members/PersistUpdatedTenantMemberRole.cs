using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Tenants.Application.Persistence.Members;

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

internal sealed class MongoPersistUpdatedTenantMemberRole(IMongoDatabase database)
{
    private readonly IMongoCollection<Tenant> _tenants = database.GetCollection<Tenant>("Tenants");

    public async Task<PersistUpdatedTenantMemberRoleOutcome> InvokeAsync(
        UpdateTenantMemberRole request,
        CancellationToken cancellationToken = default)
    {
        var tenant = await _tenants
            .Find(TenantFilters.ById(request.TenantId))
            .FirstOrDefaultAsync(cancellationToken);
        if (tenant is null)
        {
            return new TenantNotFound(request.TenantId);
        }

        var existingMember = tenant.Members.FirstOrDefault(member => member.Id == request.MemberId);
        if (existingMember is null)
        {
            return new TenantMemberNotFound(request.TenantId, request.MemberId);
        }

        var removesOwner = request.Role != TenantRole.Owner;
        if (existingMember.Role == TenantRole.Owner &&
            removesOwner &&
            tenant.Members.Count(member => member.Role == TenantRole.Owner) == 1)
        {
            return new TenantLastOwner(request.TenantId, request.MemberId);
        }

        var updatedTenant = await _tenants.FindOneAndUpdateAsync(
            removesOwner
                ? TenantFilters.ByIdWithMemberAndOwnerCanBeRemoved(
                    request.TenantId,
                    request.MemberId)
                : TenantFilters.ByIdWithMember(request.TenantId, request.MemberId),
            TenantUpdates.SetMemberRole(request.Role),
            new FindOneAndUpdateOptions<Tenant> { ReturnDocument = ReturnDocument.After },
            cancellationToken);
        var updatedMember = updatedTenant?.Members.FirstOrDefault(member => member.Id == request.MemberId);
        if (updatedMember is not null)
            return updatedMember;

        var failure = await TenantMemberMutationFailures.ResolveAsync(
            _tenants,
            request.TenantId,
            request.MemberId,
            cancellationToken);
        return failure switch
        {
            TenantNotFound error => error,
            TenantMemberNotFound error => error,
            TenantLastOwner error => error
        };
    }
}
