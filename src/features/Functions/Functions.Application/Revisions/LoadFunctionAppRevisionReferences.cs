using MongoDB.Driver;
using Pole.Features.Functions.Application;
using Pole.Features.Functions.Application.Declarations;
using Pole.Features.Functions.Application.FunctionApps;
using Pole.Features.Functions.Application.Invocations;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Application.Usage;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Revisions;

public sealed record FunctionAppRevisionReferences(
    FunctionRevision? ActiveRevision,
    FunctionRevision? PendingRevision,
    FunctionRevision? LatestConfiguration);

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
        if (revisionIds.Length == 0 && functionApp.NextRevisionNumber == 1)
            return new FunctionAppRevisionReferences(null, null, null);

        var revisions = await functionRevisions
            .Find(FunctionRevisionFilters.ReferencedConfiguration(functionApp, revisionIds))
            .ToListAsync(cancellationToken);
        return new FunctionAppRevisionReferences(
            revisions.FirstOrDefault(revision => revision.Id == functionApp.ActiveRevisionId),
            revisions.FirstOrDefault(revision => revision.Id == functionApp.PendingRevisionId),
            revisions.FirstOrDefault(revision => revision.RevisionNumber == functionApp.NextRevisionNumber - 1));
    }
}
