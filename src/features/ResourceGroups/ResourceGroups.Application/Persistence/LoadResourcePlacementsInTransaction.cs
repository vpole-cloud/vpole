using MongoDB.Driver;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.Persistence;

internal sealed class MongoLoadResourcePlacementsInTransaction(
    IMongoCollection<RuntimeCell> runtimeCells)
{
    public async Task<IReadOnlyList<ResourcePlacement>> InvokeAsync(
        string tenantId,
        IReadOnlyCollection<string> runtimeCellIds,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeCellIds);
        var ids = runtimeCellIds
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
            return [];

        var runtimeCellsForTenant = await runtimeCells
            .Find(
                session,
                RuntimeCellFilters.ByTenantAndIds(tenantId, ids))
            .ToListAsync(cancellationToken);
        return runtimeCellsForTenant
            .Select(cell => cell.ToPlacement())
            .ToArray();
    }
}
