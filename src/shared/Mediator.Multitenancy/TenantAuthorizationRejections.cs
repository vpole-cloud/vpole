using Pole.Features.Core.Multitenancy;

namespace Pole.Mediator;

/// <summary>
///     Stops a tenant-wide operation that requires platform administrator access.
/// </summary>
public sealed record TenantPlatformAdminRequired : IMediatorRejection;

/// <summary>
///     Stops a tenant-scoped operation when the active member role is insufficient.
/// </summary>
public sealed record TenantRoleRequired(
    TenantRole MinimumRole
) : IMediatorRejection;

/// <summary>A tenant-owned operation requires a resolved active tenant, including for admins.</summary>
public sealed record TenantContextRequired : IMediatorRejection;

/// <summary>The operation cannot use a tenant different from its resolved active selection.</summary>
public sealed record TenantContextMismatch : IMediatorRejection;
