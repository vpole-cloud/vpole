// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Tenants.Application.Members;

public sealed record TenantMemberAlreadyExists(
    string TenantId,
    string UserId
);

public sealed record TenantLastOwner(
    string TenantId,
    string MemberId
);

public union TenantMemberMutationError(
    TenantNotFound,
    TenantMemberNotFound,
    TenantLastOwner);
