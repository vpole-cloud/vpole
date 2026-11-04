namespace Pole.Features.Tenants.Contracts;

/// <summary>Small tenant-owned read model for consumers that need a stable display name.</summary>
public sealed record TenantIdentitySnapshot(
    string Id,
    string DisplayName);

public sealed record TenantIdentityNotFound(string TenantId);

public sealed record TenantSetNotFound(IReadOnlyList<string> TenantIds);

public sealed record TenantsExist;

public union EnsureTenantsExistOutcome(
    TenantsExist,
    TenantSetNotFound);

public union LoadTenantIdentityOutcome(
    TenantIdentitySnapshot,
    TenantIdentityNotFound);

public delegate Task<EnsureTenantsExistOutcome> EnsureTenantsExist(
    IReadOnlyCollection<string> tenantIds,
    CancellationToken cancellationToken = default);

public delegate Task<LoadTenantIdentityOutcome>
    LoadTenantIdentity(
        string tenantId,
        CancellationToken cancellationToken = default);

/// <summary>Current membership, never a role asserted by a caller.</summary>
public enum TenantMembershipRole
{
    Viewer,
    Member,
    Admin,
    Owner
}

public sealed record TenantMemberSnapshot(
    string TenantId,
    string UserId,
    TenantMembershipRole Role);

public sealed record TenantMemberMissing(
    string TenantId,
    string UserId);

public union LoadTenantMemberOutcome(
    TenantMemberSnapshot,
    TenantMemberMissing);

public delegate Task<LoadTenantMemberOutcome> LoadTenantMember(
    LoadTenantMemberRequest request,
    MongoDB.Driver.IClientSessionHandle? session = null,
    CancellationToken cancellationToken = default);

public sealed record LoadTenantMemberRequest(
    string TenantId,
    string UserId);
