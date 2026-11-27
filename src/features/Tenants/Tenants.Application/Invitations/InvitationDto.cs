// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Tenants.Application.Invitations;

public sealed record InvitationDto(
    string Id,
    string TenantId,
    string Email,
    TenantRole Role,
    long Revision,
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
        invitation.Revision,
        invitation.ExpiresAtUtc,
        invitation.Status,
        invitation.DeliveryStatus);
}

public sealed record InvitationAcceptedDto(
    string TenantId,
    string DefaultTenantId
);

public sealed record InvitationRevisionBody(
    long ExpectedRevision
);
