using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Tenants.Application.Common;

public sealed record TenantDto(
    string Id,
    string Name,
    TenantRole Role,
    bool IsDefault
);

public sealed record TenantMemberDto(
    string Id,
    string TenantId,
    string UserId,
    string Email,
    TenantRole Role,
    bool IsDefault
);

public static class TenantDtoExtensions
{
    public static TenantDto ToDto(
        this Tenant tenant,
        TenantRole role,
        bool isDefault = false) => new(tenant.Id, tenant.Name, role, isDefault);

    public static TenantMemberDto ToDto(this TenantMember member) => new(
        member.Id,
        member.TenantId,
        member.UserId,
        member.Email,
        member.Role,
        member.IsDefault);
}
