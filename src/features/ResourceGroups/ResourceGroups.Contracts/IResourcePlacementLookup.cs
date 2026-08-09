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
}
