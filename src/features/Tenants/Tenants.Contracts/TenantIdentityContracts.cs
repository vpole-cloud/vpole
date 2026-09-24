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
