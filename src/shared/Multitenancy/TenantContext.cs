namespace Pole.Features.Core.Multitenancy;

public enum TenantRole
{
    Viewer = 0,
    Member = 1,
    Admin = 2,
    Owner = 3,
}

public sealed record TenantContext(
    string TenantId,
    string TenantName,
    TenantRole Role,
    bool IsPlatformAdmin
);

public interface ITenantContextAccessor
{
    TenantContext? Current { get; set; }
}

public sealed class TenantContextAccessor : ITenantContextAccessor
{
    public TenantContext? Current { get; set; }
}

public static class TenantRoleExtensions
{
    public static bool CanRead(this TenantRole role) => role >= TenantRole.Viewer;
    public static bool CanWrite(this TenantRole role) => role >= TenantRole.Member;
    public static bool CanManageMembers(this TenantRole role) => role >= TenantRole.Admin;
    public static bool CanManageTenant(this TenantRole role) => role >= TenantRole.Owner;
}
