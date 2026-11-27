// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Encryption;
using Pole.Features.Core.Multitenancy;

namespace Pole.Features.Tenants.Domain;

public enum TenantInvitationStatus { Pending, Revoked, Accepted }

public enum InvitationDeliveryStatus { Pending, Sent, Failed }

/// <summary>One durable slot per tenant and normalized recipient; each resend changes the revision and secret.</summary>
public sealed record TenantInvitation(
    string Id,
    string TenantId,
    string Email,
    TenantRole Role,
    string IssuerId,
    string TokenHash,
    EncryptedValue DeliveryToken,
    long Revision,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    TenantInvitationStatus Status,
    InvitationDeliveryStatus DeliveryStatus,
    string? OnboardingSubject
)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public static TenantInvitation CreatePending(
        string id,
        string tenantId,
        string email,
        TenantRole role,
        string issuerId,
        string tokenHash,
        EncryptedValue deliveryToken,
        long revision,
        DateTime nowUtc) => new(
        id,
        tenantId,
        email,
        role,
        issuerId,
        tokenHash,
        deliveryToken,
        revision,
        nowUtc,
        nowUtc + Lifetime,
        TenantInvitationStatus.Pending,
        InvitationDeliveryStatus.Pending,
        null);
}

/// <summary>Immutable receipt remains valid after rotation or membership removal, without re-granting access.</summary>
public sealed record TenantInvitationAcceptance(
    string Id,
    string TenantId,
    string Subject,
    string Email,
    string MemberId,
    string DefaultTenantId,
    DateTime AcceptedAtUtc
);
