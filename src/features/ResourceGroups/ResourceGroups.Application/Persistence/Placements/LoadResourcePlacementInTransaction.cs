using MongoDB.Driver;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.Persistence;

internal sealed class MongoLoadResourcePlacementInTransaction(
    IMongoCollection<RuntimeCell> runtimeCells)
{
    public async Task<ResourcePlacement?> InvokeAsync(
        string tenantId,
        string runtimeCellId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var runtimeCell = await runtimeCells
            .Find(session, RuntimeCellFilters.ByTenantAndId(tenantId, runtimeCellId))
            .FirstOrDefaultAsync(cancellationToken);
        return runtimeCell?.ToPlacement();
    }
}
