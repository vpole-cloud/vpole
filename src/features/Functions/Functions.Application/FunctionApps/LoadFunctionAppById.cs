using MongoDB.Driver;
using Pole.Features.Functions.Application;
using Pole.Features.Functions.Application.Declarations;
using Pole.Features.Functions.Application.Invocations;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Application.Revisions;
using Pole.Features.Functions.Application.Usage;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.FunctionApps;

public delegate Task<FunctionApp?> LoadFunctionAppById(
    string tenantId,
    string functionAppId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadFunctionAppById(IMongoCollection<FunctionApp> functionApps)
{
    public async Task<FunctionApp?> InvokeAsync(
        string tenantId,
        string functionAppId,
        CancellationToken cancellationToken = default) =>
        await functionApps
            .Find(FunctionAppFilters.ByTenantAndId(tenantId, functionAppId))
            .FirstOrDefaultAsync(cancellationToken);
}
