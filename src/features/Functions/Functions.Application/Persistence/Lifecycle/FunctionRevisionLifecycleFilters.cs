using MongoDB.Driver;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Persistence.Lifecycle;

internal static class FunctionRevisionLifecycleFilters
{
    public static FilterDefinition<FunctionApp> AppByOwner(string tenantId, string functionAppId) =>
        Builders<FunctionApp>.Filter.And(
            Builders<FunctionApp>.Filter.Eq(app => app.TenantId, tenantId),
            Builders<FunctionApp>.Filter.Eq(app => app.Id, functionAppId));

    public static FilterDefinition<FunctionRevision> RevisionByOwner(
        string tenantId,
        string functionAppId,
        string revisionId) =>
        Builders<FunctionRevision>.Filter.And(
            Builders<FunctionRevision>.Filter.Eq(revision => revision.TenantId, tenantId),
            Builders<FunctionRevision>.Filter.Eq(revision => revision.FunctionAppId, functionAppId),
            Builders<FunctionRevision>.Filter.Eq(revision => revision.Id, revisionId));

    public static FilterDefinition<FunctionRevision> RevisionInStatus<TStatus>(
        string tenantId,
        string functionAppId,
        string revisionId)
        where TStatus : FunctionRevisionStatus =>
        Builders<FunctionRevision>.Filter.And(
            RevisionByOwner(tenantId, functionAppId, revisionId),
            Status<TStatus>());

    public static FilterDefinition<FunctionRevision> RevisionInOneOfStatuses(
        string tenantId,
        string functionAppId,
        string revisionId,
        params FilterDefinition<FunctionRevision>[] statuses) =>
        Builders<FunctionRevision>.Filter.And(
            RevisionByOwner(tenantId, functionAppId, revisionId),
            Builders<FunctionRevision>.Filter.Or(statuses));

    public static FilterDefinition<FunctionApp> PendingEnabledApp(
        string tenantId,
        string functionAppId,
        string revisionId) =>
        Builders<FunctionApp>.Filter.And(
            AppByOwner(tenantId, functionAppId),
            Builders<FunctionApp>.Filter.Eq(app => app.PendingRevisionId, revisionId),
            Builders<FunctionApp>.Filter.Eq(app => app.DesiredState, FunctionAppDesiredState.Enabled));

    public static FilterDefinition<FunctionApp> PendingApp(
        string tenantId,
        string functionAppId,
        string revisionId) =>
        Builders<FunctionApp>.Filter.And(
            AppByOwner(tenantId, functionAppId),
            Builders<FunctionApp>.Filter.Eq(app => app.PendingRevisionId, revisionId));

    public static FilterDefinition<FunctionRouteBinding> AvailableRoute(
        FunctionApp app) =>
        Builders<FunctionRouteBinding>.Filter.And(
            Builders<FunctionRouteBinding>.Filter.Eq(route => route.NormalizedRoute, app.FunctionRoute),
            Builders<FunctionRouteBinding>.Filter.Eq(route => route.TenantId, app.TenantId),
            Builders<FunctionRouteBinding>.Filter.Eq(route => route.FunctionAppId, app.Id),
            Builders<FunctionRouteBinding>.Filter.Ne(route => route.State, FunctionRouteBindingState.Disabled));

    public static FilterDefinition<FunctionTimerState> TimersForApp(FunctionApp app) =>
        Builders<FunctionTimerState>.Filter.And(
            Builders<FunctionTimerState>.Filter.Eq(timer => timer.TenantId, app.TenantId),
            Builders<FunctionTimerState>.Filter.Eq(timer => timer.FunctionAppId, app.Id));

    public static FilterDefinition<FunctionTimerState> TimerForUpsert(
        FunctionTimerState timer,
        FunctionApp app) =>
        Builders<FunctionTimerState>.Filter.And(
            Builders<FunctionTimerState>.Filter.Eq(candidate => candidate.Id, timer.Id),
            Builders<FunctionTimerState>.Filter.Eq(candidate => candidate.TenantId, app.TenantId),
            Builders<FunctionTimerState>.Filter.Eq(candidate => candidate.FunctionAppId, app.Id));

    public static FilterDefinition<FunctionTimerState> EnabledTimersNotIn(
        FunctionApp app,
        IReadOnlyCollection<string> activeTriggerIds) =>
        Builders<FunctionTimerState>.Filter.And(
            TimersForApp(app),
            Builders<FunctionTimerState>.Filter.Eq(timer => timer.Enabled, true),
            Builders<FunctionTimerState>.Filter.Nin(timer => timer.TriggerId, activeTriggerIds));

    public static FilterDefinition<FunctionInvocation> DurableWork(
        string tenantId,
        string functionAppId,
        string revisionId) =>
        Builders<FunctionInvocation>.Filter.And(
            Builders<FunctionInvocation>.Filter.Eq(invocation => invocation.TenantId, tenantId),
            Builders<FunctionInvocation>.Filter.Eq(invocation => invocation.FunctionAppId, functionAppId),
            Builders<FunctionInvocation>.Filter.Eq(invocation => invocation.RevisionId, revisionId),
            Builders<FunctionInvocation>.Filter.In(
                invocation => invocation.State,
                [
                    FunctionInvocationState.Pending,
                    FunctionInvocationState.Dispatching,
                    FunctionInvocationState.Running,
                    FunctionInvocationState.RetryScheduled
                ]));

    private static FilterDefinition<FunctionRevision> Status<TStatus>()
        where TStatus : FunctionRevisionStatus =>
        Builders<FunctionRevision>.Filter.OfType<FunctionRevisionStatus, TStatus>(revision => revision.Status);

    public static FilterDefinition<FunctionRevision> ProvisioningStatus() =>
        Status<ProvisioningFunctionRevisionStatus>();

    public static FilterDefinition<FunctionRevision> ReadyStatus() =>
        Status<ReadyFunctionRevisionStatus>();
}
