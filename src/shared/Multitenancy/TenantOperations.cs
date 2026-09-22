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

/// <summary>A target identity is independent of the active header selection.</summary>
public interface ITenantTargetOperation
{
    string TargetTenantId { get; }
}

/// <summary>A tenant-owned operation whose separate target must equal the active tenant.</summary>
public interface ITenantMatchingTargetOperation : ITenantOperation, ITenantTargetOperation;

/// <summary>A platform administrator may address a target without selecting an active tenant.</summary>
public interface IPlatformTenantOperation : ITenantTargetOperation;

public interface ITenantMemberOperation : ITenantTargetOperation
{
    string TenantId { get; }
    string ITenantTargetOperation.TargetTenantId => TenantId;
}

public interface ITenantMemberReadOperation : ITenantMemberOperation;

public interface ITenantMemberManageOperation : ITenantMemberOperation;

public interface ITenantMemberRoleOperation : ITenantMemberManageOperation
{
    string? MemberId { get; }
    TenantRole Role { get; }
}

public interface ITenantMemberTargetOperation : ITenantMemberManageOperation
{
    string MemberId { get; }
}
