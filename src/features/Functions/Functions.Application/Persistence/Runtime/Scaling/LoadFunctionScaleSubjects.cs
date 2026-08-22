using MongoDB.Driver;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Persistence.Runtime.Scaling;

public sealed record FunctionScaleSubject(
    FunctionApp App,
    FunctionRevision Revision,
    IReadOnlyDictionary<string, int> DurableDemandByHandler);

public delegate Task<IReadOnlyList<FunctionScaleSubject>> LoadFunctionScaleSubjects(
    CancellationToken cancellationToken = default);

public sealed class MongoLoadFunctionScaleSubjects(
    IMongoCollection<FunctionApp> functionApps,
    IMongoCollection<FunctionRevision> functionRevisions,
    IMongoCollection<FunctionInvocation> functionInvocations)
{
    public async Task<IReadOnlyList<FunctionScaleSubject>> InvokeAsync(
        CancellationToken cancellationToken = default)
    {
        var revisions = await functionRevisions.Find(FunctionScaleSubjectFilters.ScalableRevisions())
            .ToListAsync(cancellationToken);
        if (revisions.Count == 0)
            return [];

        var appIds = revisions.Select(revision => revision.FunctionAppId).Distinct().ToArray();
        var apps = await functionApps.Find(FunctionScaleSubjectFilters.AppsById(appIds))
            .ToListAsync(cancellationToken);
        var appsByOwner = apps.ToDictionary(app => (app.TenantId, app.Id));
        var revisionIds = revisions.Select(revision => revision.Id).ToArray();
        var invocations = await functionInvocations.Find(
                FunctionScaleSubjectFilters.DurableWorkForRevisions(revisionIds))
            .ToListAsync(cancellationToken);
        var durableDemand = invocations
            .GroupBy(invocation =>
                (invocation.TenantId, invocation.FunctionAppId, invocation.RevisionId, invocation.HandlerName))
            .ToDictionary(group => group.Key, group => group.Count());
        return revisions
            .Where(revision => appsByOwner.ContainsKey((revision.TenantId, revision.FunctionAppId)))
            .Select(revision => new FunctionScaleSubject(
                appsByOwner[(revision.TenantId, revision.FunctionAppId)],
                revision,
                durableDemand
                    .Where(entry => entry.Key.TenantId == revision.TenantId &&
                                    entry.Key.FunctionAppId == revision.FunctionAppId &&
                                    entry.Key.RevisionId == revision.Id)
                    .ToDictionary(entry => entry.Key.HandlerName, entry => entry.Value, StringComparer.Ordinal)))
            .ToArray();
    }
}

internal static class FunctionScaleSubjectFilters
{
    public static FilterDefinition<FunctionRevision> ScalableRevisions() =>
        Builders<FunctionRevision>.Filter.Or(
            Builders<FunctionRevision>.Filter
                .OfType<FunctionRevisionStatus, ActiveFunctionRevisionStatus>(revision => revision.Status),
            Builders<FunctionRevision>.Filter
                .OfType<FunctionRevisionStatus, DrainingFunctionRevisionStatus>(revision => revision.Status));

    public static FilterDefinition<FunctionApp> AppsById(IReadOnlyCollection<string> appIds) =>
        Builders<FunctionApp>.Filter.In(app => app.Id, appIds);

    public static FilterDefinition<FunctionInvocation> DurableWorkForRevisions(
        IReadOnlyCollection<string> revisionIds) =>
        Builders<FunctionInvocation>.Filter.And(
            Builders<FunctionInvocation>.Filter.In(invocation => invocation.RevisionId, revisionIds),
            Builders<FunctionInvocation>.Filter.In(
                invocation => invocation.State,
                [
                    FunctionInvocationState.Pending,
                    FunctionInvocationState.Dispatching,
                    FunctionInvocationState.Running,
                    FunctionInvocationState.RetryScheduled
                ]));
}
