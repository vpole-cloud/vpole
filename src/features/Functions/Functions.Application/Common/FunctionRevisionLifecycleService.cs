using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Functions.Domain;
using Pole.Features.Quotas.Contracts;
using Pole.ResultType;

namespace Pole.Features.Functions.Application.Common;

public interface IFunctionRevisionLifecycleService
{
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
    IFunctionAppDbContext dbContext,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    TimeProvider timeProvider) : IFunctionRevisionLifecycleService
{
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
            state.Revision.State == FunctionRevisionState.Active)
            return Result.Success();
        if (state.App.PendingRevisionId != revisionId)
            return Failures.Conflict("Function revision is not pending for this app.");
        if (state.App.DesiredState != FunctionAppDesiredState.Enabled)
            return Failures.Conflict("Function App is not enabled.");

        var targetAllocation = FunctionQuotaAllocation.FromRevision(state.Revision.Spec);
        return await quotaAdmissionExecutor.ExecuteAsync<FunctionQuotaAllocation, bool>(
            tenantId,
            FunctionAppQuotaResourceKey.Create(functionAppId),
            new UpdateQuotaMutation<FunctionQuotaAllocation>(state.App.AdmittedQuota, targetAllocation),
            async (session, transactionCancellationToken) =>
            {
                var now = timeProvider.GetUtcNow();
                if (state.App.ActiveRevisionId is not null)
                {
                    var drainingRevision = state.ActiveRevision!.Drain();
                    var previousUpdate = await dbContext.FunctionRevisions.UpdateOneAsync(
                        session,
                        revision => revision.TenantId == tenantId &&
                                    revision.FunctionAppId == functionAppId &&
                                    revision.Id == state.App.ActiveRevisionId &&
                                    revision.State == FunctionRevisionState.Active,
                        Builders<FunctionRevision>.Update
                            .Set(revision => revision.State, drainingRevision.State)
                            .Set(revision => revision.ReadyAtUtc, drainingRevision.ReadyAtUtc)
                            .Set(revision => revision.Failure, drainingRevision.Failure),
                        cancellationToken: transactionCancellationToken);
                    if (previousUpdate.MatchedCount != 1)
                        return Failures.Conflict("The active Function revision state changed concurrently.");
                }

                // Retire the old active revision first. The partial unique index permits only
                // one Active revision per app, including inside a Mongo transaction.
                var activeRevision = state.Revision.Activate(now);
                var revisionUpdate = await dbContext.FunctionRevisions.UpdateOneAsync(
                    session,
                    revision => revision.TenantId == tenantId &&
                                revision.FunctionAppId == functionAppId &&
                                revision.Id == revisionId &&
                                revision.State == FunctionRevisionState.Provisioning,
                    Builders<FunctionRevision>.Update
                        .Set(revision => revision.State, activeRevision.State)
                        .Set(revision => revision.ReadyAtUtc, activeRevision.ReadyAtUtc)
                        .Set(revision => revision.Failure, activeRevision.Failure),
                    cancellationToken: transactionCancellationToken);
                if (revisionUpdate.MatchedCount != 1)
                    return Failures.Conflict("Function revision state changed concurrently.");

                var appUpdate = await dbContext.FunctionApps.UpdateOneAsync(
                    session,
                    app => app.TenantId == tenantId &&
                           app.Id == functionAppId &&
                           app.PendingRevisionId == revisionId &&
                           app.DesiredState == FunctionAppDesiredState.Enabled,
                    Builders<FunctionApp>.Update
                        .Set(app => app.ActiveRevisionId, revisionId)
                        .Set(app => app.PendingRevisionId, null)
                        .Set(app => app.Status, FunctionAppStatus.Ready)
                        .Set(app => app.AdmittedQuota, targetAllocation)
                        .Set(app => app.UpdatedAtUtc, now),
                    cancellationToken: transactionCancellationToken);
                if (appUpdate.MatchedCount != 1)
                    return Failures.Conflict("Function App state changed concurrently.");

                var routeUpdate = await dbContext.FunctionRouteBindings.UpdateOneAsync(
                    session,
                    route => route.NormalizedRoute == state.App.FunctionRoute &&
                             route.TenantId == tenantId &&
                             route.FunctionAppId == functionAppId &&
                             route.State != FunctionRouteBindingState.Disabled,
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

                await dbContext.StoreEventAsync(
                    state.Revision.ToPromotedEvent(state.App.ActiveRevisionId),
                    session,
                    transactionCancellationToken);
                return true;
            },
            cancellationToken);
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
            state.Revision.State == FunctionRevisionState.Failed &&
            state.Revision.Failure == revisionFailure)
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
                var revisionUpdate = await dbContext.FunctionRevisions.UpdateOneAsync(
                    session,
                    revision => revision.TenantId == tenantId &&
                                revision.FunctionAppId == functionAppId &&
                                revision.Id == revisionId &&
                                revision.State == FunctionRevisionState.Provisioning,
                    Builders<FunctionRevision>.Update
                        .Set(revision => revision.State, failedRevision.State)
                        .Set(revision => revision.ReadyAtUtc, failedRevision.ReadyAtUtc)
                        .Set(revision => revision.Failure, failedRevision.Failure),
                    cancellationToken: transactionCancellationToken);
                if (revisionUpdate.MatchedCount != 1)
                    return Failures.Conflict("Function revision state changed concurrently.");

                var appUpdate = await dbContext.FunctionApps.UpdateOneAsync(
                    session,
                    app => app.TenantId == tenantId &&
                           app.Id == functionAppId &&
                           app.PendingRevisionId == revisionId,
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

                await dbContext.StoreEventAsync(
                    state.Revision.ToFailedEvent(revisionFailure),
                    session,
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
        using var session = await dbContext.Client.StartSessionAsync(cancellationToken: cancellationToken);
        return await session.WithTransactionAsync<Result>(async (transaction, transactionCancellationToken) =>
        {
            var revision = await dbContext.FunctionRevisions.Find(
                    transaction,
                    candidate => candidate.TenantId == tenantId &&
                                 candidate.FunctionAppId == functionAppId &&
                                 candidate.Id == revisionId)
                .FirstOrDefaultAsync(transactionCancellationToken);
            if (revision is null)
                return Failures.NotFound("Function revision not found.", revisionId);
            if (revision.State == FunctionRevisionState.Retired)
                return Result.Success();
            if (revision.State != FunctionRevisionState.Draining)
                return Failures.Conflict("Only a draining Function revision can be retired.");

            var durableWork = await dbContext.FunctionInvocations.CountDocumentsAsync(
                transaction,
                candidate => candidate.TenantId == tenantId &&
                             candidate.FunctionAppId == functionAppId &&
                             candidate.RevisionId == revisionId &&
                             (candidate.State == FunctionInvocationState.Pending ||
                              candidate.State == FunctionInvocationState.Dispatching ||
                              candidate.State == FunctionInvocationState.Running ||
                              candidate.State == FunctionInvocationState.RetryScheduled),
                new CountOptions { Limit = 1 },
                transactionCancellationToken);
            if (durableWork != 0)
                return Failures.Conflict("The Function revision still has durable invocations to process.");

            var retired = revision.Retire();
            var update = await dbContext.FunctionRevisions.UpdateOneAsync(
                transaction,
                candidate => candidate.TenantId == tenantId &&
                             candidate.FunctionAppId == functionAppId &&
                             candidate.Id == revisionId &&
                             candidate.State == FunctionRevisionState.Draining,
                Builders<FunctionRevision>.Update.Set(candidate => candidate.State, retired.State),
                cancellationToken: transactionCancellationToken);
            if (update.MatchedCount != 1)
                return Failures.Conflict("Function revision state changed concurrently.");

            await dbContext.StoreEventAsync(revision.ToRetiredEvent(), transaction, transactionCancellationToken);
            return Result.Success();
        }, cancellationToken: cancellationToken);
    }

    private async Task<Result<PendingRevisionState>> LoadStateAsync(
        string tenantId,
        string functionAppId,
        string revisionId,
        CancellationToken cancellationToken)
    {
        var app = await dbContext.FunctionApps.Find(candidate =>
                candidate.TenantId == tenantId && candidate.Id == functionAppId)
            .FirstOrDefaultAsync(cancellationToken);
        if (app is null)
            return Failures.NotFound("Function App not found.", functionAppId);

        var revision = await dbContext.FunctionRevisions.Find(candidate =>
                candidate.TenantId == tenantId &&
                candidate.FunctionAppId == functionAppId &&
                candidate.Id == revisionId)
            .FirstOrDefaultAsync(cancellationToken);
        if (revision is null)
            return Failures.NotFound("Function revision not found.", revisionId);

        FunctionRevision? activeRevision = null;
        if (app.ActiveRevisionId is not null)
        {
            activeRevision = await dbContext.FunctionRevisions.Find(candidate =>
                    candidate.TenantId == tenantId &&
                    candidate.FunctionAppId == functionAppId &&
                    candidate.Id == app.ActiveRevisionId)
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
        var existing = await dbContext.FunctionTimerStates.Find(
                session,
                timer => timer.TenantId == app.TenantId && timer.FunctionAppId == app.Id)
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

            await dbContext.FunctionTimerStates.ReplaceOneAsync(
                session,
                candidate => candidate.Id == reconciled.Id &&
                             candidate.TenantId == app.TenantId &&
                             candidate.FunctionAppId == app.Id,
                reconciled,
                new ReplaceOptions { IsUpsert = true },
                cancellationToken);
        }

        var disabledFilter = Builders<FunctionTimerState>.Filter.And(
            Builders<FunctionTimerState>.Filter.Eq(timer => timer.TenantId, app.TenantId),
            Builders<FunctionTimerState>.Filter.Eq(timer => timer.FunctionAppId, app.Id),
            Builders<FunctionTimerState>.Filter.Eq(timer => timer.Enabled, true),
            Builders<FunctionTimerState>.Filter.Nin(timer => timer.TriggerId, activeTriggerIds));
        await dbContext.FunctionTimerStates.UpdateManyAsync(
            session,
            disabledFilter,
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
