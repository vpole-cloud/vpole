using MongoDB.Driver;
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

internal sealed class MongoPersistRemovedTenantMember(IMongoDatabase database)
{
    private readonly IMongoCollection<Tenant> _tenants = database.GetCollection<Tenant>("Tenants");

    public async Task<PersistRemovedTenantMemberOutcome> InvokeAsync(
        RemoveTenantMember request,
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

        if (existingMember.Role == TenantRole.Owner &&
            tenant.Members.Count(member => member.Role == TenantRole.Owner) == 1)
        {
            return new TenantLastOwner(request.TenantId, request.MemberId);
        }

        var result = await _tenants.UpdateOneAsync(
            TenantFilters.ByIdWithMemberAndOwnerCanBeRemoved(
                request.TenantId,
                request.MemberId),
            TenantUpdates.RemoveMember(request.MemberId),
            cancellationToken: cancellationToken);
        if (result.ModifiedCount != 0)
            return new TenantMemberRemoved(request.MemberId);

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
