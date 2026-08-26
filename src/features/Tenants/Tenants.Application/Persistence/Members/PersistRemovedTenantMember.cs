using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Domain;
using Pole.ResultType;

namespace Pole.Features.Tenants.Application.Persistence.Members;

public sealed record RemoveTenantMember(
    string TenantId,
    string MemberId);

public delegate Task<Result<Unit, TenantMemberMutationError>> PersistRemovedTenantMember(
    RemoveTenantMember request,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistRemovedTenantMember(IMongoDatabase database)
{
    private readonly IMongoCollection<Tenant> _tenants = database.GetCollection<Tenant>("Tenants");

    public async Task<Result<Unit, TenantMemberMutationError>> InvokeAsync(
        RemoveTenantMember request,
        CancellationToken cancellationToken = default)
    {
        var tenant = await _tenants
            .Find(TenantFilters.ById(request.TenantId))
            .FirstOrDefaultAsync(cancellationToken);
        if (tenant is null)
        {
            return new Err<TenantMemberMutationError>(
                new TenantMemberMutationError(new TenantNotFound(request.TenantId)));
        }

        var existingMember = tenant.Members.FirstOrDefault(member => member.Id == request.MemberId);
        if (existingMember is null)
        {
            return new Err<TenantMemberMutationError>(
                new TenantMemberMutationError(
                    new TenantMemberNotFound(request.TenantId, request.MemberId)));
        }

        if (existingMember.Role == TenantRole.Owner &&
            tenant.Members.Count(member => member.Role == TenantRole.Owner) == 1)
        {
            return new Err<TenantMemberMutationError>(
                new TenantMemberMutationError(new TenantLastOwner(request.TenantId, request.MemberId)));
        }

        var result = await _tenants.UpdateOneAsync(
            existingMember.Role == TenantRole.Owner
                ? TenantFilters.ByIdWithMemberAndAnotherOwner(
                    request.TenantId,
                    request.MemberId)
                : TenantFilters.ByIdWithMember(request.TenantId, request.MemberId),
            TenantUpdates.RemoveMember(request.MemberId),
            cancellationToken: cancellationToken);
        if (result.ModifiedCount != 0)
            return new Ok<Unit>(Unit.Value);

        return await TenantMemberMutationFailures.ResolveAsync(
            _tenants,
            request.TenantId,
            request.MemberId,
            cancellationToken);
    }
}
