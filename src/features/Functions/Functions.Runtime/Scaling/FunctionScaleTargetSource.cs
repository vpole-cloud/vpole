using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Domain;
using Pole.ResultType;

namespace Pole.Features.Functions.Runtime.Scaling;

public sealed record FunctionScaleTarget(
    FunctionApp App,
    FunctionRevision Revision,
    bool IsServing,
    IReadOnlyDictionary<string, int> DurableDemandByHandler);

public interface IFunctionScaleTargetSource
{
    Task<IReadOnlyList<FunctionScaleTarget>> ListAsync(CancellationToken cancellationToken);
    Task<Result> MarkRetiredAsync(FunctionScaleTarget target, CancellationToken cancellationToken);
}

public sealed class MongoFunctionScaleTargetSource(
    IFunctionAppDbContext dbContext,
    IServiceScopeFactory scopeFactory)
    : IFunctionScaleTargetSource
{
    public async Task<IReadOnlyList<FunctionScaleTarget>> ListAsync(CancellationToken cancellationToken)
    {
        var revisions = await dbContext.FunctionRevisions.Find(
                Builders<FunctionRevision>.Filter.Or(
                    Builders<FunctionRevision>.Filter
                        .OfType<FunctionRevisionStatus, ActiveFunctionRevisionStatus>(revision => revision.Status),
                    Builders<FunctionRevision>.Filter
                        .OfType<FunctionRevisionStatus, DrainingFunctionRevisionStatus>(revision => revision.Status)))
            .ToListAsync(cancellationToken);
        if (revisions.Count == 0)
            return [];

        var appIds = revisions.Select(revision => revision.FunctionAppId).Distinct().ToArray();
        var apps = await dbContext.FunctionApps.Find(app => appIds.Contains(app.Id))
            .ToListAsync(cancellationToken);
        var appsByOwner = apps.ToDictionary(app => (app.TenantId, app.Id));
        var revisionIds = revisions.Select(revision => revision.Id).ToArray();
        var invocations = await dbContext.FunctionInvocations.Find(invocation =>
                revisionIds.Contains(invocation.RevisionId) &&
                (invocation.State == FunctionInvocationState.Pending ||
                 invocation.State == FunctionInvocationState.Dispatching ||
                 invocation.State == FunctionInvocationState.Running ||
                 invocation.State == FunctionInvocationState.RetryScheduled))
            .ToListAsync(cancellationToken);
        var durableDemand = invocations
            .GroupBy(invocation =>
                (invocation.TenantId, invocation.FunctionAppId, invocation.RevisionId, invocation.HandlerName))
            .ToDictionary(group => group.Key, group => group.Count());
        return revisions
            .Where(revision => appsByOwner.ContainsKey((revision.TenantId, revision.FunctionAppId)))
            .Select(revision =>
            {
                var app = appsByOwner[(revision.TenantId, revision.FunctionAppId)];
                var serving = revision.Status is ActiveFunctionRevisionStatus &&
                              app.DesiredState == FunctionAppDesiredState.Enabled &&
                              app.Status == FunctionAppStatus.Ready &&
                              string.Equals(app.ActiveRevisionId, revision.Id, StringComparison.Ordinal);
                var handlerDemand = durableDemand
                    .Where(entry => entry.Key.TenantId == revision.TenantId &&
                                    entry.Key.FunctionAppId == revision.FunctionAppId &&
                                    entry.Key.RevisionId == revision.Id)
                    .ToDictionary(entry => entry.Key.HandlerName, entry => entry.Value, StringComparer.Ordinal);
                return new FunctionScaleTarget(app, revision, serving, handlerDemand);
            })
            .ToArray();
    }

    public async Task<Result> MarkRetiredAsync(
        FunctionScaleTarget target,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var lifecycleService = scope.ServiceProvider.GetRequiredService<IFunctionRevisionLifecycleService>();
        return await lifecycleService.RetireAsync(
            target.Revision.TenantId,
            target.Revision.FunctionAppId,
            target.Revision.Id,
            cancellationToken);
    }
}
