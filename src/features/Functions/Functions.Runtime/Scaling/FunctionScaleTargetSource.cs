using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Domain;
using Pole.ResultType;

namespace Pole.Features.Functions.Runtime.Scaling;

public sealed record FunctionScaleTarget(
    FunctionApp App,
    FunctionRevision Revision,
    bool IsServing);

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
        var revisions = await dbContext.FunctionRevisions.Find(revision =>
                revision.State == FunctionRevisionState.Active ||
                revision.State == FunctionRevisionState.Draining)
            .ToListAsync(cancellationToken);
        if (revisions.Count == 0)
            return [];

        var appIds = revisions.Select(revision => revision.FunctionAppId).Distinct().ToArray();
        var apps = await dbContext.FunctionApps.Find(app => appIds.Contains(app.Id))
            .ToListAsync(cancellationToken);
        var appsByOwner = apps.ToDictionary(app => (app.TenantId, app.Id));
        return revisions
            .Where(revision => appsByOwner.ContainsKey((revision.TenantId, revision.FunctionAppId)))
            .Select(revision =>
            {
                var app = appsByOwner[(revision.TenantId, revision.FunctionAppId)];
                var serving = revision.State == FunctionRevisionState.Active &&
                              app.DesiredState == FunctionAppDesiredState.Enabled &&
                              app.Status == FunctionAppStatus.Ready &&
                              string.Equals(app.ActiveRevisionId, revision.Id, StringComparison.Ordinal);
                return new FunctionScaleTarget(app, revision, serving);
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
