using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Functions.Application.Persistence.Lifecycle;
using Pole.Features.Functions.Domain;
using Pole.Features.Quotas.Contracts;

namespace Pole.Features.Functions.Application.Common;

public interface IFunctionRevisionLifecycleService
{
    Task<FunctionRevisionLifecycleOutcome> MarkReadyAsync(
        string tenantId,
        string functionAppId,
        string revisionId,
        CancellationToken cancellationToken = default);

    Task<FunctionRevisionLifecycleOutcome> PromoteAsync(
        string tenantId,
        string functionAppId,
        string revisionId,
        CancellationToken cancellationToken = default);

    Task<FunctionRevisionLifecycleOutcome> FailAsync(
        string tenantId,
        string functionAppId,
        string revisionId,
        FunctionRevisionFailure failure,
        CancellationToken cancellationToken = default);

    Task<FunctionRevisionLifecycleOutcome> RetireAsync(
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
    RunMongoOutcomeTransaction<FunctionRevisionLifecycleOutcome> runMongoTransaction,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    TimeProvider timeProvider) : IFunctionRevisionLifecycleService
{
    public async Task<FunctionRevisionLifecycleOutcome> MarkReadyAsync(
        string tenantId,
        string functionAppId,
        string revisionId,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadStateAsync(tenantId, functionAppId, revisionId, cancellationToken);
        PendingRevisionState state;
        switch (loaded)
        {
            case PendingRevisionState value:
                state = value;
                break;
            case FunctionAppNotFound error:
                return error;
            case FunctionRevisionNotFound error:
                return error;
            default:
                throw new InvalidOperationException("Unsupported Function revision lifecycle state.");
        }
        if (state.Revision.Status is ReadyFunctionRevisionStatus)
            return Success();
        if (state.Revision.Status is not ProvisioningFunctionRevisionStatus)
            return new FunctionRevisionNotProvisioning(revisionId);
        if (state.App.PendingRevisionId != revisionId)
            return new FunctionRevisionNotPending(functionAppId, revisionId);
        if (state.App.DesiredState != FunctionAppDesiredState.Enabled)
            return new FunctionAppIsNotEnabled(functionAppId);

        var ready = state.Revision.MarkReady(timeProvider.GetUtcNow());
        var update = await functionRevisions.UpdateOneAsync(
            FunctionRevisionLifecycleFilters.RevisionInStatus<ProvisioningFunctionRevisionStatus>(
                tenantId,
                functionAppId,
                revisionId),
            Builders<FunctionRevision>.Update.Set(revision => revision.Status, ready.Status),
            cancellationToken: cancellationToken);
        if (update.MatchedCount == 1)
            return Success();

        var current = await functionRevisions.Find(
                FunctionRevisionLifecycleFilters.RevisionByOwner(
                    tenantId,
                    functionAppId,
                    revisionId))
            .FirstOrDefaultAsync(cancellationToken);
        return current?.Status is ReadyFunctionRevisionStatus or ActiveFunctionRevisionStatus
            ? Success()
            : new FunctionRevisionConcurrencyConflict(functionAppId, revisionId);
    }

    public async Task<FunctionRevisionLifecycleOutcome> PromoteAsync(
        string tenantId,
        string functionAppId,
        string revisionId,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadStateAsync(tenantId, functionAppId, revisionId, cancellationToken);
        PendingRevisionState state;
        switch (loaded)
        {
            case PendingRevisionState value:
                state = value;
                break;
            case FunctionAppNotFound error:
                return error;
            case FunctionRevisionNotFound error:
                return error;
            default:
                throw new InvalidOperationException("Unsupported Function revision lifecycle state.");
        }
        if (state.App.ActiveRevisionId == revisionId &&
            state.App.PendingRevisionId is null &&
            state.Revision.Status is ActiveFunctionRevisionStatus)
            return Success();
        if (state.App.PendingRevisionId != revisionId)
            return new FunctionRevisionNotPending(functionAppId, revisionId);
        if (state.App.DesiredState != FunctionAppDesiredState.Enabled)
            return new FunctionAppIsNotEnabled(functionAppId);
        if (state.Revision.Status is not ReadyFunctionRevisionStatus)
            return new FunctionRevisionNotReady(revisionId);

        var targetAllocation = FunctionQuotaAllocation.FromRevision(state.Revision.Spec);
        var promotion = await quotaAdmissionExecutor.ExecuteOutcomeAsync<
            FunctionQuotaAllocation,
            FunctionRevisionLifecycleOutcome>(
            tenantId,
            FunctionAppQuotaResourceKey.Create(functionAppId),
            new UpdateQuotaMutation<FunctionQuotaAllocation>(state.App.AdmittedQuota, targetAllocation),
            quotaError => quotaError,
            async (transaction, transactionCancellationToken) =>
            {
                var now = timeProvider.GetUtcNow();
                if (state.App.ActiveRevisionId is not null)
                {
                    var drainingRevision = state.ActiveRevision!.Drain();
                    var previousUpdate = await functionRevisions.UpdateOneAsync(
                        transaction.Session,
                        FunctionRevisionLifecycleFilters.RevisionInStatus<ActiveFunctionRevisionStatus>(
                            tenantId,
                            functionAppId,
                            state.App.ActiveRevisionId),
                        Builders<FunctionRevision>.Update
                            .Set(revision => revision.Status, drainingRevision.Status),
                        cancellationToken: transactionCancellationToken);
                    if (previousUpdate.MatchedCount != 1)
                    {
                        return transaction.Abort(new FunctionRevisionConcurrencyConflict(
                            functionAppId,
                            state.App.ActiveRevisionId));
                    }
                }

                // Retire the old active revision first. The partial unique index permits only
                // one Active revision per app, including inside a Mongo transaction.
                var activeRevision = state.Revision.Activate();
                var revisionUpdate = await functionRevisions.UpdateOneAsync(
                    transaction.Session,
                    FunctionRevisionLifecycleFilters.RevisionInStatus<ReadyFunctionRevisionStatus>(
                        tenantId,
                        functionAppId,
                        revisionId),
                    Builders<FunctionRevision>.Update
                        .Set(revision => revision.Status, activeRevision.Status),
                    cancellationToken: transactionCancellationToken);
                if (revisionUpdate.MatchedCount != 1)
                {
                    return transaction.Abort(new FunctionRevisionConcurrencyConflict(
                        functionAppId,
                        revisionId));
                }

                var appUpdate = await functionApps.UpdateOneAsync(
                    transaction.Session,
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
                {
                    return transaction.Abort(new FunctionAppConcurrencyConflict(functionAppId));
                }

                var routeUpdate = await functionRouteBindings.UpdateOneAsync(
                    transaction.Session,
                    FunctionRevisionLifecycleFilters.AvailableRoute(state.App),
                    Builders<FunctionRouteBinding>.Update
                        .Set(route => route.HttpRoutes, CreateHttpRoutes(state.Revision))
                        .Set(route => route.State, FunctionRouteBindingState.Active)
                        .Set(route => route.ActiveRevisionId, revisionId)
                        .Set(route => route.ActiveRevisionNumber, state.Revision.RevisionNumber)
                        .Set(route => route.UpdatedAtUtc, now),
                    cancellationToken: transactionCancellationToken);
                if (routeUpdate.MatchedCount != 1)
                {
                    return transaction.Abort(new FunctionRouteBindingUnavailable(functionAppId));
                }

                await ReconcileTimersAsync(
                    transaction.Session,
                    state.App,
                    state.Revision,
                    now,
                    transactionCancellationToken);

                await storeOutboxWrites(
                    transaction.Session,
                    [OutboxWrite.Create(
                        state.Revision.ToPromotedEvent(state.App.ActiveRevisionId))],
                    transactionCancellationToken);
                return transaction.Commit(Success());
            },
            cancellationToken);
        if (promotion is FunctionRevisionLifecycleCompleted)
            return promotion;

        var concurrent = await LoadStateAsync(tenantId, functionAppId, revisionId, cancellationToken);
        if (concurrent is PendingRevisionState currentState &&
            currentState.App.ActiveRevisionId == revisionId &&
            currentState.App.PendingRevisionId is null &&
            currentState.Revision.Status is ActiveFunctionRevisionStatus)
        {
            return Success();
        }

        return promotion;
    }

    public async Task<FunctionRevisionLifecycleOutcome> FailAsync(
        string tenantId,
        string functionAppId,
        string revisionId,
        FunctionRevisionFailure revisionFailure,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadStateAsync(tenantId, functionAppId, revisionId, cancellationToken);
        PendingRevisionState state;
        switch (loaded)
        {
            case PendingRevisionState value:
                state = value;
                break;
            case FunctionAppNotFound error:
                return error;
            case FunctionRevisionNotFound error:
                return error;
            default:
                throw new InvalidOperationException("Unsupported Function revision lifecycle state.");
        }
        if (state.App.PendingRevisionId is null &&
            state.Revision.Status is FailedFunctionRevisionStatus failed &&
            failed.Failure == revisionFailure)
            return Success();
        if (state.App.PendingRevisionId != revisionId)
            return new FunctionRevisionNotPending(functionAppId, revisionId);
        if (state.App.DesiredState != FunctionAppDesiredState.Enabled)
            return new FunctionAppIsNotEnabled(functionAppId);

        var targetAllocation = state.ActiveRevision is null
            ? new FunctionQuotaAllocation(1, 0, 0, 0)
            : FunctionQuotaAllocation.FromRevision(state.ActiveRevision.Spec);
        return await quotaAdmissionExecutor.ExecuteOutcomeAsync<
            FunctionQuotaAllocation,
            FunctionRevisionLifecycleOutcome>(
            tenantId,
            FunctionAppQuotaResourceKey.Create(functionAppId),
            new UpdateQuotaMutation<FunctionQuotaAllocation>(state.App.AdmittedQuota, targetAllocation),
            quotaError => quotaError,
            async (transaction, transactionCancellationToken) =>
            {
                var now = timeProvider.GetUtcNow();
                var failedRevision = state.Revision.Fail(revisionFailure);
                var revisionUpdate = await functionRevisions.UpdateOneAsync(
                    transaction.Session,
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
                {
                    return transaction.Abort(new FunctionRevisionConcurrencyConflict(
                        functionAppId,
                        revisionId));
                }

                var appUpdate = await functionApps.UpdateOneAsync(
                    transaction.Session,
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
                {
                    return transaction.Abort(new FunctionAppConcurrencyConflict(functionAppId));
                }

                await storeOutboxWrites(
                    transaction.Session,
                    [OutboxWrite.Create(state.Revision.ToFailedEvent(revisionFailure))],
                    transactionCancellationToken);
                return transaction.Commit(Success());
            },
            cancellationToken);
    }

    public async Task<FunctionRevisionLifecycleOutcome> RetireAsync(
        string tenantId,
        string functionAppId,
        string revisionId,
        CancellationToken cancellationToken = default)
    {
        return await runMongoTransaction(async (transaction, transactionCancellationToken) =>
        {
            var revision = await functionRevisions.Find(
                    transaction.Session,
                    FunctionRevisionLifecycleFilters.RevisionByOwner(
                        tenantId,
                        functionAppId,
                        revisionId))
                .FirstOrDefaultAsync(transactionCancellationToken);
            if (revision is null)
            {
                return transaction.Abort(new FunctionRevisionNotFound(functionAppId, revisionId));
            }
            if (revision.Status is RetiredFunctionRevisionStatus)
                return transaction.Commit(Success());
            if (revision.Status is not DrainingFunctionRevisionStatus)
            {
                return transaction.Abort(new FunctionRevisionNotDraining(revisionId));
            }

            var durableWork = await functionInvocations.CountDocumentsAsync(
                transaction.Session,
                FunctionRevisionLifecycleFilters.DurableWork(
                    tenantId,
                    functionAppId,
                    revisionId),
                new CountOptions { Limit = 1 },
                transactionCancellationToken);
            if (durableWork != 0)
            {
                return transaction.Abort(new FunctionRevisionHasDurableInvocations(
                    functionAppId,
                    revisionId));
            }

            var retired = revision.Retire();
            var update = await functionRevisions.UpdateOneAsync(
                transaction.Session,
                FunctionRevisionLifecycleFilters.RevisionInStatus<DrainingFunctionRevisionStatus>(
                    tenantId,
                    functionAppId,
                    revisionId),
                Builders<FunctionRevision>.Update.Set(candidate => candidate.Status, retired.Status),
                cancellationToken: transactionCancellationToken);
            if (update.MatchedCount != 1)
            {
                return transaction.Abort(new FunctionRevisionConcurrencyConflict(
                    functionAppId,
                    revisionId));
            }

            await storeOutboxWrites(
                transaction.Session,
                [OutboxWrite.Create(revision.ToRetiredEvent())],
                transactionCancellationToken);
            return transaction.Commit(Success());
        }, cancellationToken);
    }

    private async Task<FunctionRevisionLifecycleStateOutcome> LoadStateAsync(
        string tenantId,
        string functionAppId,
        string revisionId,
        CancellationToken cancellationToken)
    {
        var app = await functionApps.Find(
                FunctionRevisionLifecycleFilters.AppByOwner(tenantId, functionAppId))
            .FirstOrDefaultAsync(cancellationToken);
        if (app is null)
            return new FunctionAppNotFound(functionAppId);

        var revision = await functionRevisions.Find(
                FunctionRevisionLifecycleFilters.RevisionByOwner(
                    tenantId,
                    functionAppId,
                    revisionId))
            .FirstOrDefaultAsync(cancellationToken);
        if (revision is null)
            return new FunctionRevisionNotFound(functionAppId, revisionId);

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
            {
                throw new InvalidOperationException(
                    "Function App references an active revision that does not exist.");
            }
        }

        return new PendingRevisionState(app, revision, activeRevision);
    }

    private static FunctionRevisionLifecycleOutcome Success() =>
        new FunctionRevisionLifecycleCompleted();

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

    private union FunctionRevisionLifecycleStateOutcome(
        PendingRevisionState,
        FunctionAppNotFound,
        FunctionRevisionNotFound);
}
