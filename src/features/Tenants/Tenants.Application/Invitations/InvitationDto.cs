using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Tenants.Application.Invitations;

public sealed record InvitationDto(
    string Id,
    string TenantId,
    string Email,
    TenantRole Role,
    DateTime ExpiresAtUtc,
    TenantInvitationStatus Status,
    InvitationDeliveryStatus DeliveryStatus
)
{
    public static InvitationDto From(TenantInvitation invitation) => new(
        invitation.Id,
        invitation.TenantId,
        invitation.Email,
        invitation.Role,
        invitation.ExpiresAtUtc,
        invitation.Status,
        invitation.DeliveryStatus);
}

public sealed record InvitationAcceptedDto(
    string TenantId,
    string DefaultTenantId
);
