// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Security.Claims;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Realtime.Contracts;
using Pole.Features.Tenants.Contracts;

namespace Pole.Features.Realtime.Application;

public sealed class RealtimeAccess(
    LoadTenantMember loadMember,
    LoadTenantIdentity loadTenant,
    RealtimeTopicRegistry registry
)
{
    public RealtimeTopic? FindTopic(string name) => registry.Find(name);
    public bool IsAvailable(string name) => registry.Find(name) is not null && registry.IsEnabled(name);

    public async Task<bool> AllowsAsync(
        ClaimsPrincipal principal,
        RealtimeSubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        if (principal.Identity?.IsAuthenticated != true ||
            string.IsNullOrWhiteSpace(principal.FindFirstValue(Claims.UserId)))
            return false;
        var topic = registry.Find(request.Topic);
        if (topic is null || request.ResourceId is { Length: > 200 })
            return false;
        if (topic.Scope == RealtimeScope.User)
            return request.TenantId is null && request.ResourceId == principal.FindFirstValue(Claims.UserId);
        var admin = principal.HasClaim(Claims.Roles, Roles.Admin);
        if (topic.Scope == RealtimeScope.Platform)
            return admin && request.TenantId is null;
        if (!ObjectId.TryParse(request.TenantId, out _))
            return false;
        if (admin)
            return await loadTenant(request.TenantId!, cancellationToken) is TenantIdentitySnapshot;
        return await loadMember(
            new LoadTenantMemberRequest(request.TenantId!, principal.FindFirstValue(Claims.UserId)!),
            cancellationToken: cancellationToken) is TenantMemberSnapshot;
    }
}
