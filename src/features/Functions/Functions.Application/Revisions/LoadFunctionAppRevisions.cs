using Pole.Features.Functions.Application.Usage;
using Pole.Features.Functions.Application.Invocations;
using Pole.Features.Functions.Application.FunctionApps;
using MongoDB.Driver;
using Pole.Features.Functions.Application;
using Pole.Features.Functions.Application.Declarations;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Revisions;

public delegate Task<IReadOnlyList<FunctionRevision>> LoadFunctionAppRevisions(
    FunctionApp functionApp,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadFunctionAppRevisions(
    IMongoCollection<FunctionRevision> functionRevisions)
{
    public async Task<IReadOnlyList<FunctionRevision>> InvokeAsync(
        FunctionApp functionApp,
        CancellationToken cancellationToken = default) =>
        await functionRevisions
            .Find(FunctionRevisionFilters.ByFunctionApp(functionApp.TenantId, functionApp.Id))
            .SortByDescending(revision => revision.RevisionNumber)
            .ToListAsync(cancellationToken);
}
