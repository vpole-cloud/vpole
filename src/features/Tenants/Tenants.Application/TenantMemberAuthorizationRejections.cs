// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Mediator;

namespace Pole.Features.Tenants.Application;

public sealed record TenantAuthenticationRequired : IMediatorRejection;

public sealed record TenantUserIdentityRequired : IMediatorRejection;

public sealed record TenantNotFound(
    string TenantId
) : IMediatorRejection;

public sealed record TenantMembershipRequired(
    string TenantId
) : IMediatorRejection;

public sealed record TenantMemberNotFound(
    string TenantId,
    string MemberId
) : IMediatorRejection;
