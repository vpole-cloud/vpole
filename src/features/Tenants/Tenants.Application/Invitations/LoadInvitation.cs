using MongoDB.Driver;
using Pole.Features.Tenants.Application.Endpoints.Invitations;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Mongo;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Tenants.Application.Invitations;

public sealed record InvitationPreviewDto(
    string TenantName,
    string Email,
    TenantRole Role,
    DateTime ExpiresAtUtc
);

public sealed class MongoLoadInvitation(
    IMongoCollection<Tenant> tenants,
    IMongoCollection<TenantInvitation> invitations,
    TimeProvider timeProvider
)
{
    public async Task<TenantInvitation?> ValidAsync(string token, CancellationToken ct)
    {
        var invitation = await invitations.Find(InvitationFilters.ByTokenHash(InvitationIdentity.Hash(token)))
            .SingleOrDefaultAsync(ct);
        if (invitation is null || invitation.Status != TenantInvitationStatus.Pending ||
            invitation.ExpiresAtUtc <= timeProvider.GetUtcNow().UtcDateTime)
            return null;
        var tenant = await tenants.Find(TenantFilters.ById(invitation.TenantId)).SingleOrDefaultAsync(ct);
        return tenant is not null && InvitationIdentity.CanGrant(
            tenant,
            new InvitationActor(invitation.IssuerId),
            invitation.Role)
            ? invitation
            : null;
    }

    public async Task<InvitationPreviewDto?> PreviewAsync(string token, CancellationToken ct)
    {
        var invitation = await ValidAsync(token, ct);
        if (invitation is null)
            return null;
        var tenant = await tenants.Find(TenantFilters.ById(invitation.TenantId)).SingleOrDefaultAsync(ct);
        return tenant is null
            ? null
            : new InvitationPreviewDto(tenant.Name, invitation.Email, invitation.Role, invitation.ExpiresAtUtc);
    }

    public async Task<GetTenantInvitationsOutcome> ListAsync(
        string tenantId,
        InvitationActor actor,
        CancellationToken ct)
    {
        var tenant = await tenants.Find(TenantFilters.ById(tenantId)).SingleOrDefaultAsync(ct);
        if (tenant is null)
            return new TenantNotFound(tenantId);
        if (!InvitationIdentity.CanGrant(tenant, actor, TenantRole.Viewer))
            return new InvitationForbidden();
        return (await invitations.Find(InvitationFilters.ByTenant(tenant.Id)).ToListAsync(ct))
            .OrderByDescending(item => item.CreatedAtUtc)
            .Select(InvitationDto.From)
            .ToArray();
    }
}
