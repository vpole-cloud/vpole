using MongoDB.Driver;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Mongo;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.Placements;

internal sealed class MongoLoadResourcePlacement(
    IMongoCollection<RuntimeCell> runtimeCells
)
{
    public async Task<ResourcePlacement?> InvokeAsync(
        string tenantId,
        string runtimeCellId,
        CancellationToken cancellationToken = default)
    {
        var runtimeCell = await runtimeCells.Find(RuntimeCellFilters.ByTenantAndId(tenantId, runtimeCellId))
            .FirstOrDefaultAsync(cancellationToken);
        return runtimeCell?.ToPlacement();
    }
}
