using MongoDB.Driver;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Tenants.Application.Persistence.Members;

public union PersistAddedTenantMemberOutcome(
    TenantMember,
    TenantNotFound,
    TenantMemberAlreadyExists);

public delegate Task<PersistAddedTenantMemberOutcome> PersistAddedTenantMember(
    TenantMember member,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistAddedTenantMember(IMongoDatabase database)
{
    private readonly IMongoCollection<Tenant> _tenants = database.GetCollection<Tenant>("Tenants");

    public async Task<PersistAddedTenantMemberOutcome> InvokeAsync(
        TenantMember member,
        CancellationToken cancellationToken = default)
    {
        var updatedTenant = await _tenants.FindOneAndUpdateAsync(
            TenantFilters.ByIdWithoutMemberUser(member.TenantId, member.UserId),
            TenantUpdates.AddMember(member),
            new FindOneAndUpdateOptions<Tenant> { ReturnDocument = ReturnDocument.After },
            cancellationToken);
        if (updatedTenant is not null)
            return member;

        var tenant = await _tenants
            .Find(TenantFilters.ById(member.TenantId))
            .FirstOrDefaultAsync(cancellationToken);
        return tenant is null
            ? new TenantNotFound(member.TenantId)
            : new TenantMemberAlreadyExists(member.TenantId, member.UserId);
    }
}
