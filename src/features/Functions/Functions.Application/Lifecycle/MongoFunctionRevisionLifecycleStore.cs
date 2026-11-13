using MongoDB.Driver;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Domain;
using Pole.Scheduling;

namespace Pole.Features.Functions.Application.Lifecycle;

internal sealed class MongoFunctionRevisionLifecycleStore(
    IMongoCollection<FunctionApp> functionApps,
    IMongoCollection<FunctionRevision> functionRevisions,
    IMongoCollection<FunctionTimerState> functionTimerStates
)
{
    public async Task<FunctionRevisionLifecycleStateOutcome> LoadAsync(
        string tenantId,
        string functionAppId,
        string revisionId,
        CancellationToken cancellationToken)
    {
        var app = await functionApps.Find(FunctionRevisionLifecycleFilters.AppByOwner(tenantId, functionAppId))
            .FirstOrDefaultAsync(cancellationToken);
        if (app is null)
            return new FunctionAppNotFound(functionAppId);

        var revision = await functionRevisions.Find(
                FunctionRevisionLifecycleFilters.RevisionByOwner(tenantId, functionAppId, revisionId))
            .FirstOrDefaultAsync(cancellationToken);
        if (revision is null)
            return new FunctionRevisionNotFound(functionAppId, revisionId);

        FunctionRevision? activeRevision = null;
        if (app.ActiveRevisionId is not null)
        {
            activeRevision = await functionRevisions.Find(
                    FunctionRevisionLifecycleFilters.RevisionByOwner(tenantId, functionAppId, app.ActiveRevisionId))
                .FirstOrDefaultAsync(cancellationToken);
            if (activeRevision is null)
                throw new InvalidOperationException("Function App references an active revision that does not exist.");
        }

        return new FunctionRevisionLifecycleState(app, revision, activeRevision);
    }

    public async Task ReconcileTimersAsync(
        IClientSessionHandle session,
        FunctionApp app,
        FunctionRevision revision,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var existing = await functionTimerStates.Find(session, FunctionRevisionLifecycleFilters.TimersForApp(app))
            .ToListAsync(cancellationToken);
        var existingByTrigger = existing.ToDictionary(timer => timer.TriggerId, StringComparer.Ordinal);
        var activeTriggerIds = new List<string>();

        foreach (var handler in revision.Spec.Handlers)
        {
            if (handler.Trigger is not TimerFunctionTriggerSpec timer)
                continue;

            activeTriggerIds.Add(timer.TriggerId);
            var schedule = UtcCronSchedule.Parse(timer.Cron);
            FunctionTimerState reconciled;
            if (existingByTrigger.TryGetValue(timer.TriggerId, out var current))
            {
                var nextOccurrence = current.ScheduleVersion == timer.ScheduleVersion
                    ? current.NextOccurrenceUtc
                    : schedule.GetNextOccurrence(nowUtc);
                reconciled = current.Reconcile(
                    revision.Id,
                    handler.Name,
                    timer.Cron,
                    timer.ScheduleVersion,
                    timer.ConcurrencyPolicy,
                    timer.MisfirePolicy,
                    timer.Retry,
                    nextOccurrence,
                    nowUtc);
            }
            else
            {
                reconciled = FunctionTimerState.Create(
                    app.TenantId,
                    app.Id,
                    revision.Id,
                    timer.TriggerId,
                    handler.Name,
                    timer.Cron,
                    timer.ScheduleVersion,
                    timer.ConcurrencyPolicy,
                    timer.MisfirePolicy,
                    timer.Retry,
                    schedule.GetNextOccurrence(nowUtc),
                    nowUtc);
            }

            await functionTimerStates.ReplaceOneAsync(
                session,
                FunctionRevisionLifecycleFilters.TimerForUpsert(reconciled, app),
                reconciled,
                new ReplaceOptions { IsUpsert = true },
                cancellationToken);
        }

        await functionTimerStates.UpdateManyAsync(
            session,
            FunctionRevisionLifecycleFilters.EnabledTimersNotIn(app, activeTriggerIds),
            Builders<FunctionTimerState>.Update
                .Set(timer => timer.Enabled, false)
                .Set(timer => timer.Lease, null)
                .Set(timer => timer.UpdatedAtUtc, nowUtc),
            cancellationToken: cancellationToken);
    }
}

internal sealed record FunctionRevisionLifecycleState(
    FunctionApp App,
    FunctionRevision Revision,
    FunctionRevision? ActiveRevision
);

internal union FunctionRevisionLifecycleStateOutcome(
    FunctionRevisionLifecycleState,
    FunctionAppNotFound,
    FunctionRevisionNotFound);
