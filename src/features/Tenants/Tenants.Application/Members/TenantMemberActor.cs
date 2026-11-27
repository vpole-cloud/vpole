// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Tenants.Application.Members;

public sealed record TenantMemberActor(
    string Subject,
    bool PlatformAdmin
)
{
    public static TenantMemberActor From(string subject, IReadOnlyCollection<string>? roles) =>
        new(subject, roles?.Contains(Roles.Admin) == true);

    public bool CanManage(
        Tenant tenant,
        TenantMember target,
        TenantRole? nextRole)
    {
        if (PlatformAdmin)
            return true;
        var current = tenant.Members.FirstOrDefault(member => member.UserId == Subject);
        return current is not null && current.Role.CanManageMembers() &&
            ((target.Role != TenantRole.Owner && nextRole != TenantRole.Owner) || current.Role.CanManageTenant());
    }
}

public sealed record TenantMemberManagementForbidden;
