using Pole.ResultType;

namespace Pole.Features.Tenants.Contracts;

/// <summary>Small tenant-owned read model for consumers that need a stable display name.</summary>
public sealed record TenantIdentitySnapshot(string Id, string DisplayName);

public sealed record TenantIdentityNotFound(string TenantId);

public sealed record TenantSetNotFound(IReadOnlyList<string> TenantIds);

public delegate Task<Result<Unit, TenantSetNotFound>> EnsureTenantsExist(
    IReadOnlyCollection<string> tenantIds,
    CancellationToken cancellationToken = default);

public delegate Task<Result<TenantIdentitySnapshot, TenantIdentityNotFound>>
    LoadTenantIdentity(
        string tenantId,
        CancellationToken cancellationToken = default);
