namespace Pole.Features.Core.Multitenancy;

public static class TenantHeaders
{
    public const string TenantId = "X-Pole-Tenant-ID";
}

public interface ITenantOperation
{
    string? TenantId { get; }
}

public interface ITenantReadOperation : ITenantOperation;

public interface ITenantWriteOperation : ITenantOperation;

public interface ITenantCreateOperation : ITenantWriteOperation;

/// <summary>An administrator operates on the route tenant without a tenant header.</summary>
public interface IPlatformTenantOperation
{
    string TenantId { get; }
}

public interface ITenantMemberOperation
{
    string TenantId { get; }
}

public interface ITenantMemberReadOperation : ITenantMemberOperation;

public interface ITenantMemberManageOperation : ITenantMemberOperation;

public interface ITenantMemberRoleOperation : ITenantMemberManageOperation
{
    TenantRole Role { get; }
}

public interface ITenantMemberTargetOperation : ITenantMemberManageOperation
{
    string MemberId { get; }
}
