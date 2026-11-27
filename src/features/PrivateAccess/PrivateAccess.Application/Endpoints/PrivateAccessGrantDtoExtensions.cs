// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.PrivateAccess.Domain;

namespace Pole.Features.PrivateAccess.Application.Endpoints;

internal static class PrivateAccessGrantDtoExtensions
{
    public static PrivateAccessGrantDto ToDto(this PrivateAccessGrant grant) => new(
        grant.Id,
        grant.TenantId,
        grant.SubjectUserId,
        grant.ResourceType,
        grant.ResourceId,
        grant.ResourceDeploymentId,
        grant.EndpointId,
        grant.CreatedAtUtc,
        grant.ExpiresAtUtc,
        grant.State is RevokedPrivateAccessGrant);
}
