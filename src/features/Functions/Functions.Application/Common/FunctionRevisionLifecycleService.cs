using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Functions.Application.Persistence.Lifecycle;
using Pole.Features.Functions.Domain;
using Pole.Features.Quotas.Contracts;
using Pole.ResultType;

namespace Pole.Features.Functions.Application.Common;

public interface IFunctionRevisionLifecycleService
{
    Task<Result> MarkReadyAsync(
        string tenantId,
        string functionAppId,
        string revisionId,
        CancellationToken cancellationToken = default);

    Task<Result> PromoteAsync(
        string tenantId,
        string functionAppId,
        string revisionId,
        CancellationToken cancellationToken = default);

    Task<Result> FailAsync(
        string tenantId,
        string functionAppId,
        string revisionId,
        FunctionRevisionFailure failure,
        CancellationToken cancellationToken = default);

    Task<Result> RetireAsync(
        string tenantId,
        string functionAppId,
        string revisionId,
        CancellationToken cancellationToken = default);
}

public sealed class FunctionRevisionLifecycleService(
    IMongoCollection<FunctionApp> functionApps,
    IMongoCollection<FunctionRevision> functionRevisions,
    IMongoCollection<FunctionRouteBinding> functionRouteBindings,
    IMongoCollection<FunctionTimerState> functionTimerStates,
    IMongoCollection<FunctionInvocation> functionInvocations,
    StoreOutboxWrites storeOutboxWrites,
    RunMongoTransaction runMongoTransaction,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    TimeProvider timeProvider) : IFunctionRevisionLifecycleService
{
    public async Task<Result> MarkReadyAsync(
        string tenantId,
        string functionAppId,
        string revisionId,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadStateAsync(tenantId, functionAppId, revisionId, cancellationToken);
        if (!loaded.TryGetValue(out var state, out var failure))
            return failure;
        if (state.Revision.Status is ReadyFunctionRevisionStatus)
            return Result.Success();
        if (state.Revision.Status is not ProvisioningFunctionRevisionStatus)
            return Failures.Conflict("Only a provisioning Function revision can become ready.");
        if (state.App.PendingRevisionId != revisionId)
            return Failures.Conflict("Function revision is not pending for this app.");
        if (state.App.DesiredState != FunctionAppDesiredState.Enabled)
            return Failures.Conflict("Function App is not enabled.");

        var ready = state.Revision.MarkReady(timeProvider.GetUtcNow());
        var update = await functionRevisions.UpdateOneAsync(
            FunctionRevisionLifecycleFilters.RevisionInStatus<ProvisioningFunctionRevisionStatus>(
                tenantId,
                functionAppId,
                revisionId),
            Builders<FunctionRevision>.Update.Set(revision => revision.Status, ready.Status),
            cancellationToken: cancellationToken);
        if (update.MatchedCount == 1)
            return Result.Success();

        var current = await functionRevisions.Find(
                FunctionRevisionLifecycleFilters.RevisionByOwner(
                    tenantId,
                    functionAppId,
                    revisionId))
            .FirstOrDefaultAsync(cancellationToken);
        return current?.Status is ReadyFunctionRevisionStatus or ActiveFunctionRevisionStatus
            ? Result.Success()
            : Failures.Conflict("Function revision status changed concurrently.");
    }

    public async Task<Result> PromoteAsync(
        string tenantId,
        string functionAppId,
        string revisionId,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadStateAsync(tenantId, functionAppId, revisionId, cancellationToken);
        if (!loaded.TryGetValue(out var state, out var failure))
            return failure;
        if (state.App.ActiveRevisionId == revisionId &&
            state.App.PendingRevisionId is null &&
            state.Revision.Status is ActiveFunctionRevisionStatus)
            return Result.Success();
        if (state.App.PendingRevisionId != revisionId)
            return Failures.Conflict("Function revision is not pending for this app.");
        if (state.App.DesiredState != FunctionAppDesiredState.Enabled)
            return Failures.Conflict("Function App is not enabled.");
        if (state.Revision.Status is not ReadyFunctionRevisionStatus)
            return Failures.Conflict("Function revision is not ready for promotion.");

        var targetAllocation = FunctionQuotaAllocation.FromRevision(state.Revision.Spec);
        var promotion = await quotaAdmissionExecutor.ExecuteAsync<FunctionQuotaAllocation, bool>(
            tenantId,
            FunctionAppQuotaResourceKey.Create(functionAppId),
            new UpdateQuotaMutation<FunctionQuotaAllocation>(state.App.AdmittedQuota, targetAllocation),
            async (session, transactionCancellationToken) =>
            {
                var now = timeProvider.GetUtcNow();
                if (state.App.ActiveRevisionId is not null)
                {
                    var drainingRevision = state.ActiveRevision!.Drain();
                    var previousUpdate = await functionRevisions.UpdateOneAsync(
                        session,
                        FunctionRevisionLifecycleFilters.RevisionInStatus<ActiveFunctionRevisionStatus>(
                            tenantId,
                            functionAppId,
                            state.App.ActiveRevisionId),
                        Builders<FunctionRevision>.Update
                            .Set(revision => revision.Status, drainingRevision.Status),
                        cancellationToken: transactionCancellationToken);
                    if (previousUpdate.MatchedCount != 1)
                        return Failures.Conflict("The active Function revision state changed concurrently.");
                }

                // Retire the old active revision first. The partial unique index permits only
                // one Active revision per app, including inside a Mongo transaction.
                var activeRevision = state.Revision.Activate();
                var revisionUpdate = await functionRevisions.UpdateOneAsync(
                    session,
                    FunctionRevisionLifecycleFilters.RevisionInStatus<ReadyFunctionRevisionStatus>(
                        tenantId,
                        functionAppId,
                        revisionId),
                    Builders<FunctionRevision>.Update
                        .Set(revision => revision.Status, activeRevision.Status),
                    cancellationToken: transactionCancellationToken);
                if (revisionUpdate.MatchedCount != 1)
                    return Failures.Conflict("Function revision state changed concurrently.");

                var appUpdate = await functionApps.UpdateOneAsync(
                    session,
                    FunctionRevisionLifecycleFilters.PendingEnabledApp(
                        tenantId,
                        functionAppId,
                        revisionId),
                    Builders<FunctionApp>.Update
                        .Set(app => app.ActiveRevisionId, revisionId)
                        .Set(app => app.PendingRevisionId, null)
                        .Set(app => app.Status, FunctionAppStatus.Ready)
                        .Set(app => app.AdmittedQuota, targetAllocation)
                        .Set(app => app.UpdatedAtUtc, now),
                    cancellationToken: transactionCancellationToken);
                if (appUpdate.MatchedCount != 1)
                    return Failures.Conflict("Function App state changed concurrently.");

                var routeUpdate = await functionRouteBindings.UpdateOneAsync(
                    session,
                    FunctionRevisionLifecycleFilters.AvailableRoute(state.App),
                    Builders<FunctionRouteBinding>.Update
                        .Set(route => route.HttpRoutes, CreateHttpRoutes(state.Revision))
                        .Set(route => route.State, FunctionRouteBindingState.Active)
                        .Set(route => route.ActiveRevisionId, revisionId)
                        .Set(route => route.ActiveRevisionNumber, state.Revision.RevisionNumber)
                        .Set(route => route.UpdatedAtUtc, now),
                    cancellationToken: transactionCancellationToken);
                if (routeUpdate.MatchedCount != 1)
                    return Failures.Conflict("Function route binding is unavailable.");

                await ReconcileTimersAsync(
                    session,
                    state.App,
                    state.Revision,
                    now,
                    transactionCancellationToken);

                await storeOutboxWrites(
                    session,
                    [OutboxWrite.Create(
                        state.Revision.ToPromotedEvent(state.App.ActiveRevisionId))],
                    transactionCancellationToken);
                return true;
            },
            cancellationToken);
        if (promotion is ISuccess)
            return promotion;

        var concurrent = await LoadStateAsync(tenantId, functionAppId, revisionId, cancellationToken);
        if (concurrent.TryGetValue(out var currentState, out _) &&
            currentState.App.ActiveRevisionId == revisionId &&
            currentState.App.PendingRevisionId is null &&
            currentState.Revision.Status is ActiveFunctionRevisionStatus)
        {
            return Result.Success();
        }

        return promotion;
    }

    public async Task<Result> FailAsync(
        string tenantId,
        string functionAppId,
        string revisionId,
        FunctionRevisionFailure revisionFailure,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadStateAsync(tenantId, functionAppId, revisionId, cancellationToken);
        if (!loaded.TryGetValue(out var state, out var failure))
            return failure;
        if (state.App.PendingRevisionId is null &&
            state.Revision.Status is FailedFunctionRevisionStatus failed &&
            failed.Failure == revisionFailure)
            return Result.Success();
        if (state.App.PendingRevisionId != revisionId)
            return Failures.Conflict("Function revision is not pending for this app.");
        if (state.App.DesiredState != FunctionAppDesiredState.Enabled)
            return Failures.Conflict("Function App is not enabled.");

        var targetAllocation = state.ActiveRevision is null
            ? new FunctionQuotaAllocation(1, 0, 0, 0)
            : FunctionQuotaAllocation.FromRevision(state.ActiveRevision.Spec);
        return await quotaAdmissionExecutor.ExecuteAsync<FunctionQuotaAllocation, bool>(
            tenantId,
            FunctionAppQuotaResourceKey.Create(functionAppId),
            new UpdateQuotaMutation<FunctionQuotaAllocation>(state.App.AdmittedQuota, targetAllocation),
            async (session, transactionCancellationToken) =>
            {
                var now = timeProvider.GetUtcNow();
                var failedRevision = state.Revision.Fail(revisionFailure);
                var revisionUpdate = await functionRevisions.UpdateOneAsync(
                    session,
                    FunctionRevisionLifecycleFilters.RevisionInOneOfStatuses(
                        tenantId,
                        functionAppId,
                        revisionId,
                        FunctionRevisionLifecycleFilters.ProvisioningStatus(),
                        FunctionRevisionLifecycleFilters.ReadyStatus()),
                    Builders<FunctionRevision>.Update
                        .Set(revision => revision.Status, failedRevision.Status),
                    cancellationToken: transactionCancellationToken);
                if (revisionUpdate.MatchedCount != 1)
                    return Failures.Conflict("Function revision state changed concurrently.");

                var appUpdate = await functionApps.UpdateOneAsync(
                    session,
                    FunctionRevisionLifecycleFilters.PendingApp(
                        tenantId,
                        functionAppId,
                        revisionId),
                    Builders<FunctionApp>.Update
                        .Set(app => app.PendingRevisionId, null)
                        .Set(app => app.Status, state.App.ActiveRevisionId is null
                            ? FunctionAppStatus.Failed
                            : FunctionAppStatus.Ready)
                        .Set(app => app.AdmittedQuota, targetAllocation)
                        .Set(app => app.UpdatedAtUtc, now),
                    cancellationToken: transactionCancellationToken);
                if (appUpdate.MatchedCount != 1)
                    return Failures.Conflict("Function App state changed concurrently.");

                await storeOutboxWrites(
                    session,
                    [OutboxWrite.Create(state.Revision.ToFailedEvent(revisionFailure))],
                    transactionCancellationToken);
                return true;
            },
            cancellationToken);
    }

    public async Task<Result> RetireAsync(
        string tenantId,
        string functionAppId,
        string revisionId,
        CancellationToken cancellationToken = default)
    {
        return await runMongoTransaction(async (transaction, transactionCancellationToken) =>
        {
            var revision = await functionRevisions.Find(
                    transaction,
                    FunctionRevisionLifecycleFilters.RevisionByOwner(
                        tenantId,
                        functionAppId,
                        revisionId))
                .FirstOrDefaultAsync(transactionCancellationToken);
            if (revision is null)
                return Failures.NotFound("Function revision not found.", revisionId);
            if (revision.Status is RetiredFunctionRevisionStatus)
                return Result.Success();
            if (revision.Status is not DrainingFunctionRevisionStatus)
                return Failures.Conflict("Only a draining Function revision can be retired.");

            var durableWork = await functionInvocations.CountDocumentsAsync(
                transaction,
                FunctionRevisionLifecycleFilters.DurableWork(
                    tenantId,
                    functionAppId,
                    revisionId),
                new CountOptions { Limit = 1 },
                transactionCancellationToken);
            if (durableWork != 0)
                return Failures.Conflict("The Function revision still has durable invocations to process.");

            var retired = revision.Retire();
            var update = await functionRevisions.UpdateOneAsync(
                transaction,
                FunctionRevisionLifecycleFilters.RevisionInStatus<DrainingFunctionRevisionStatus>(
                    tenantId,
                    functionAppId,
                    revisionId),
                Builders<FunctionRevision>.Update.Set(candidate => candidate.Status, retired.Status),
                cancellationToken: transactionCancellationToken);
            if (update.MatchedCount != 1)
                return Failures.Conflict("Function revision state changed concurrently.");

            await storeOutboxWrites(
                transaction,
                [OutboxWrite.Create(revision.ToRetiredEvent())],
                transactionCancellationToken);
            return Result.Success();
        }, cancellationToken);
    }

    private async Task<Result<PendingRevisionState>> LoadStateAsync(
        string tenantId,
        string functionAppId,
        string revisionId,
        CancellationToken cancellationToken)
    {
        var app = await functionApps.Find(
                FunctionRevisionLifecycleFilters.AppByOwner(tenantId, functionAppId))
            .FirstOrDefaultAsync(cancellationToken);
        if (app is null)
            return Failures.NotFound("Function App not found.", functionAppId);

        var revision = await functionRevisions.Find(
                FunctionRevisionLifecycleFilters.RevisionByOwner(
                    tenantId,
                    functionAppId,
                    revisionId))
            .FirstOrDefaultAsync(cancellationToken);
        if (revision is null)
            return Failures.NotFound("Function revision not found.", revisionId);

        FunctionRevision? activeRevision = null;
        if (app.ActiveRevisionId is not null)
        {
            activeRevision = await functionRevisions.Find(
                    FunctionRevisionLifecycleFilters.RevisionByOwner(
                        tenantId,
                        functionAppId,
                        app.ActiveRevisionId))
                .FirstOrDefaultAsync(cancellationToken);
            if (activeRevision is null)
                return Failures.Conflict("The active Function revision is missing.");
        }

        return new PendingRevisionState(app, revision, activeRevision);
    }

    private static ImmutableList<FunctionHttpRouteBinding> CreateHttpRoutes(FunctionRevision revision) =>
        revision.Spec.Handlers
            .Where(handler => handler.Trigger is HttpFunctionTriggerSpec)
            .Select(handler =>
            {
                var trigger = (HttpFunctionTriggerSpec)handler.Trigger;
                return new FunctionHttpRouteBinding(
                    trigger.TriggerId,
                    handler.Name,
                    trigger.Route,
                    trigger.Methods,
                    trigger.Exposure);
            })
            .ToImmutableList();

    private async Task ReconcileTimersAsync(
        IClientSessionHandle session,
        FunctionApp app,
        FunctionRevision revision,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var existing = await functionTimerStates.Find(
                session,
                FunctionRevisionLifecycleFilters.TimersForApp(app))
            .ToListAsync(cancellationToken);
        var existingByTrigger = existing.ToDictionary(timer => timer.TriggerId, StringComparer.Ordinal);
        var activeTriggerIds = new List<string>();

        foreach (var handler in revision.Spec.Handlers)
        {
            if (handler.Trigger is not TimerFunctionTriggerSpec timer)
                continue;

            activeTriggerIds.Add(timer.TriggerId);
            var schedule = FunctionCronSchedule.Parse(timer.Cron);
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

    private sealed record PendingRevisionState(
        FunctionApp App,
        FunctionRevision Revision,
        FunctionRevision? ActiveRevision);
}
