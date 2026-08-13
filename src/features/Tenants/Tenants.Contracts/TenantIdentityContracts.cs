using Pole.ResultType;

namespace Pole.Features.Tenants.Contracts;

/// <summary>Small tenant-owned read model for consumers that need a stable display name.</summary>
public sealed record TenantIdentitySnapshot(string Id, string DisplayName);

public interface ITenantIdentityLookup
{
    Task<Result<TenantIdentitySnapshot>> GetAsync(
        string tenantId,
        CancellationToken cancellationToken = default);
}
