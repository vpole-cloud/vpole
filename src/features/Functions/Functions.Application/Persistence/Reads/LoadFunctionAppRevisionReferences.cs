using MongoDB.Driver;
using Pole.Features.Functions.Application.Persistence;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Persistence.Reads;

public sealed record FunctionAppRevisionReferences(
    FunctionRevision? ActiveRevision,
    FunctionRevision? PendingRevision);

public delegate Task<FunctionAppRevisionReferences> LoadFunctionAppRevisionReferences(
    FunctionApp functionApp,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadFunctionAppRevisionReferences(
    IMongoCollection<FunctionRevision> functionRevisions)
{
    public async Task<FunctionAppRevisionReferences> InvokeAsync(
        FunctionApp functionApp,
        CancellationToken cancellationToken = default)
    {
        var revisionIds = new[]
            {
                functionApp.ActiveRevisionId,
                functionApp.PendingRevisionId
            }
            .Where(id => id is not null)
            .Cast<string>()
            .ToArray();
        if (revisionIds.Length == 0)
            return new FunctionAppRevisionReferences(null, null);

        var revisions = await functionRevisions
            .Find(FunctionRevisionFilters.ByFunctionAppAndIds(
                functionApp.TenantId,
                functionApp.Id,
                revisionIds))
            .ToListAsync(cancellationToken);
        return new FunctionAppRevisionReferences(
            revisions.FirstOrDefault(revision => revision.Id == functionApp.ActiveRevisionId),
            revisions.FirstOrDefault(revision => revision.Id == functionApp.PendingRevisionId));
    }
}
