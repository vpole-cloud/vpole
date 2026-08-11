using MongoDB.Driver;

namespace Pole.Features.ResourceGroups.Contracts;

public interface IResourcePlacementLookup
{
    Task<ResourcePlacement?> FindAsync(
        string tenantId,
        string runtimeCellId,
        CancellationToken cancellationToken = default);

    Task<ResourcePlacement?> FindAsync(
        string tenantId,
        string runtimeCellId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the requested runtime-cell placement snapshots within the caller's
    /// transaction. Results are unordered; callers should match them by
    /// <see cref="ResourcePlacement.RuntimeCellId"/>.
    /// </summary>
    Task<IReadOnlyList<ResourcePlacement>> FindManyAsync(
        string tenantId,
        IReadOnlyCollection<string> runtimeCellIds,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);
}
