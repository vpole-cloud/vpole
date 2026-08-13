using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.Services;

public sealed class ResourcePlacementLookup(IResourceGroupDbContext dbContext)
    : IResourcePlacementLookup
{
    public async Task<ResourcePlacement?> FindAsync(
        string tenantId,
        string runtimeCellId,
        CancellationToken cancellationToken = default)
    {
        var runtimeCell = await dbContext.RuntimeCells
            .Find(RuntimeCellFilter(tenantId, runtimeCellId))
            .FirstOrDefaultAsync(cancellationToken);

        return runtimeCell?.ToPlacement();
    }

    public async Task<ResourcePlacement?> FindAsync(
        string tenantId,
        string runtimeCellId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var runtimeCell = await dbContext.RuntimeCells
            .Find(session, RuntimeCellFilter(tenantId, runtimeCellId))
            .FirstOrDefaultAsync(cancellationToken);

        return runtimeCell?.ToPlacement();
    }

    public async Task<IReadOnlyList<ResourcePlacement>> FindManyAsync(
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

        var runtimeCells = await dbContext.RuntimeCells
            .Find(
                session,
                Builders<RuntimeCell>.Filter.And(
                    Builders<RuntimeCell>.Filter.ForTenant(tenantId),
                    Builders<RuntimeCell>.Filter.In(cell => cell.Id, ids)))
            .ToListAsync(cancellationToken);

        return runtimeCells
            .Select(cell => cell.ToPlacement())
            .ToArray();
    }

    private static FilterDefinition<RuntimeCell> RuntimeCellFilter(
        string tenantId,
        string runtimeCellId) =>
        Builders<RuntimeCell>.Filter.And(
            Builders<RuntimeCell>.Filter.Eq(cell => cell.Id, runtimeCellId),
            Builders<RuntimeCell>.Filter.ForTenant(tenantId));
}
