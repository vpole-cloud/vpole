using MongoDB.Driver;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Persistence.RuntimeCells;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.Persistence.Placements;

internal sealed class MongoLoadResourcePlacementsInTransaction(
    IMongoCollection<RuntimeCell> runtimeCells)
{
    public async Task<IReadOnlyList<ResourcePlacement>> InvokeAsync(
        LoadResourcePlacementsInTransactionRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request.RuntimeCellIds);
        var ids = request.RuntimeCellIds
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
            return [];

        var runtimeCellsForTenant = await runtimeCells
            .Find(
                session,
                RuntimeCellFilters.ByTenantAndIds(request.TenantId, ids))
            .ToListAsync(cancellationToken);
        return runtimeCellsForTenant
            .Select(cell => cell.ToPlacement())
            .ToArray();
    }
}
