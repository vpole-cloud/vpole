using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Contracts;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Tenants.Application.Persistence.Identity;

internal sealed class MongoLoadTenantMember(IMongoDatabase database)
{
    private readonly IMongoCollection<Tenant> tenants = database.GetCollection<Tenant>("Tenants");

    public async Task<LoadTenantMemberOutcome> InvokeAsync(
        LoadTenantMemberRequest request,
        IClientSessionHandle? session = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserId);

        Tenant? tenant;
        if (session is null)
        {
            tenant = await tenants.Find(TenantFilters.ById(request.TenantId))
                .FirstOrDefaultAsync(cancellationToken);
        }
        else
        {
            if (!session.IsInTransaction)
                throw new InvalidOperationException("Membership fence requires a Mongo transaction.");

            // This write conflicts with a concurrent membership edit, which also updates the
            // tenant document. A grant or admission cannot commit against a stale member list.
            tenant = await tenants.FindOneAndUpdateAsync(
                session,
                TenantFilters.ById(request.TenantId),
                Builders<Tenant>.Update.Inc(item => item.ReferenceFence, 1),
                new FindOneAndUpdateOptions<Tenant> { ReturnDocument = ReturnDocument.After },
                cancellationToken);
        }

        var member = tenant?.Members.FirstOrDefault(item => item.UserId == request.UserId);
        return member is null
            ? new TenantMemberMissing(request.TenantId, request.UserId)
            : new TenantMemberSnapshot(request.TenantId, request.UserId, member.Role switch
            {
                TenantRole.Viewer => TenantMembershipRole.Viewer,
                TenantRole.Member => TenantMembershipRole.Member,
                TenantRole.Admin => TenantMembershipRole.Admin,
                TenantRole.Owner => TenantMembershipRole.Owner,
                _ => throw new InvalidOperationException("Unexpected tenant membership role.")
            });
    }
}
