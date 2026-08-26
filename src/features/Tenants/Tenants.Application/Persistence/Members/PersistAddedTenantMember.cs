using MongoDB.Driver;
using Pole.Features.Tenants.Domain;
using Pole.ResultType;

namespace Pole.Features.Tenants.Application.Persistence.Members;

public delegate Task<Result<TenantMember, TenantMemberAdditionError>> PersistAddedTenantMember(
    TenantMember member,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistAddedTenantMember(IMongoDatabase database)
{
    private readonly IMongoCollection<Tenant> _tenants = database.GetCollection<Tenant>("Tenants");

    public async Task<Result<TenantMember, TenantMemberAdditionError>> InvokeAsync(
        TenantMember member,
        CancellationToken cancellationToken = default)
    {
        var updatedTenant = await _tenants.FindOneAndUpdateAsync(
            TenantFilters.ByIdWithoutMemberUser(member.TenantId, member.UserId),
            TenantUpdates.AddMember(member),
            new FindOneAndUpdateOptions<Tenant> { ReturnDocument = ReturnDocument.After },
            cancellationToken);
        if (updatedTenant is not null)
            return new Ok<TenantMember>(member);

        var tenant = await _tenants
            .Find(TenantFilters.ById(member.TenantId))
            .FirstOrDefaultAsync(cancellationToken);
        return tenant is null
            ? new Err<TenantMemberAdditionError>(
                new TenantMemberAdditionError(new TenantNotFound(member.TenantId)))
            : new Err<TenantMemberAdditionError>(
                new TenantMemberAdditionError(
                    new TenantMemberAlreadyExists(member.TenantId, member.UserId)));
    }
}
