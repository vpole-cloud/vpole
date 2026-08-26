using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Domain;
using Pole.ResultType;

namespace Pole.Features.Tenants.Application.Persistence;

public sealed record UpdateTenantMemberRole(
    string TenantId,
    string MemberId,
    TenantRole Role);

public delegate Task<Result<TenantMember, TenantMemberMutationError>>
    PersistUpdatedTenantMemberRole(
        UpdateTenantMemberRole request,
        CancellationToken cancellationToken = default);

internal sealed class MongoPersistUpdatedTenantMemberRole(IMongoDatabase database)
{
    private readonly IMongoCollection<Tenant> _tenants = database.GetCollection<Tenant>("Tenants");

    public async Task<Result<TenantMember, TenantMemberMutationError>> InvokeAsync(
        UpdateTenantMemberRole request,
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

        var removesOwner = request.Role != TenantRole.Owner;
        if (existingMember.Role == TenantRole.Owner &&
            removesOwner &&
            tenant.Members.Count(member => member.Role == TenantRole.Owner) == 1)
        {
            return new Err<TenantMemberMutationError>(
                new TenantMemberMutationError(new TenantLastOwner(request.TenantId, request.MemberId)));
        }

        var updatedTenant = await _tenants.FindOneAndUpdateAsync(
            removesOwner
                ? TenantFilters.ByIdWithMemberAndAnotherOwner(
                    request.TenantId,
                    request.MemberId)
                : TenantFilters.ByIdWithMember(request.TenantId, request.MemberId),
            TenantUpdates.SetMemberRole(request.Role),
            new FindOneAndUpdateOptions<Tenant> { ReturnDocument = ReturnDocument.After },
            cancellationToken);
        var updatedMember = updatedTenant?.Members.FirstOrDefault(member => member.Id == request.MemberId);
        if (updatedMember is not null)
            return new Ok<TenantMember>(updatedMember);

        return await TenantMemberMutationFailures.ResolveAsync(
            _tenants,
            request.TenantId,
            request.MemberId,
            cancellationToken);
    }
}
