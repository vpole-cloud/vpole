using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Functions.Application;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Declarations;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Domain;
using Pole.Features.Quotas.Contracts;

namespace Pole.Features.Functions.Application.Lifecycle;

public delegate Task<FunctionRevisionLifecycleOutcome> PromoteFunctionRevision(
    string tenantId,
    string functionAppId,
    string revisionId,
    CancellationToken cancellationToken = default);

internal sealed class MongoPromoteFunctionRevision(
    IMongoCollection<FunctionApp> functionApps,
    IMongoCollection<FunctionRevision> functionRevisions,
    IMongoCollection<FunctionRouteBinding> functionRouteBindings,
    StoreOutboxWrites storeOutboxWrites,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    MongoFunctionRevisionLifecycleStore lifecycleStore,
    TimeProvider timeProvider)
{
    public async Task<FunctionRevisionLifecycleOutcome> InvokeAsync(
        string tenantId,
        string functionAppId,
        string revisionId,
        CancellationToken cancellationToken = default)
    {
        var loaded = await lifecycleStore.LoadAsync(
            tenantId,
            functionAppId,
            revisionId,
            cancellationToken);
        FunctionRevisionLifecycleState state;
        switch (loaded)
        {
            case FunctionRevisionLifecycleState value:
                state = value;
                break;
            case FunctionAppNotFound error:
                return error;
            case FunctionRevisionNotFound error:
                return error;
            default:
                throw new InvalidOperationException("Unsupported Function revision lifecycle state.");
        }
        if (state.Revision.Status is ActiveFunctionRevisionStatus)
        {
            var app = state.App;
            if (app.ActiveRevisionId != revisionId || app.PendingRevisionId is not null)
            {
                // A concurrent promotion can commit between the app and revision reads.
                app = await functionApps.Find(
                        FunctionRevisionLifecycleFilters.AppByOwner(tenantId, functionAppId))
                    .FirstOrDefaultAsync(cancellationToken);
            }
            if (app is not null && app.ActiveRevisionId == revisionId && app.PendingRevisionId is null)
                return new FunctionRevisionLifecycleCompleted();
        }
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
            new ExecuteQuotaAdmissionRequest<global::Pole.Features.Functions.Domain.FunctionQuotaAllocation,
                global::Pole.Features.Functions.Application.Common.FunctionRevisionLifecycleOutcome>(
                TenantId: tenantId,
                ResourceKey: FunctionAppQuotaResourceKey.Create(functionAppId),
                Mutation: new UpdateQuotaMutation<FunctionQuotaAllocation>(state.App.AdmittedQuota, targetAllocation),
                QuotaError: quotaError => quotaError,
                MutationCallback: async (transaction, transactionCancellationToken) =>
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
                        return transaction.Abort(new FunctionAppConcurrencyConflict(functionAppId));

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
                        return transaction.Abort(new FunctionRouteBindingUnavailable(functionAppId));

                    await lifecycleStore.ReconcileTimersAsync(
                        transaction.Session,
                        state.App,
                        state.Revision,
                        now,
                        transactionCancellationToken);

                    await storeOutboxWrites(
                        transaction.Session,
                        [OutboxWrite.Create(
                            state.Revision.ToPromotedEvent(
                                state.App,
                                state.App.ActiveRevisionId))],
                        transactionCancellationToken);
                    return transaction.Commit(new FunctionRevisionLifecycleCompleted());
                }),
            cancellationToken: cancellationToken);
        if (promotion is FunctionRevisionLifecycleCompleted)
            return promotion;

        var concurrent = await lifecycleStore.LoadAsync(
            tenantId,
            functionAppId,
            revisionId,
            cancellationToken);
        if (concurrent is FunctionRevisionLifecycleState currentState &&
            currentState.App.ActiveRevisionId == revisionId &&
            currentState.App.PendingRevisionId is null &&
            currentState.Revision.Status is ActiveFunctionRevisionStatus)
        {
            return new FunctionRevisionLifecycleCompleted();
        }

        return promotion;
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
}
