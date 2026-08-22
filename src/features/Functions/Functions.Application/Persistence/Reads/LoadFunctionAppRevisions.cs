using MongoDB.Driver;
using Pole.Features.Functions.Application.Persistence;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Persistence.Reads;

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
