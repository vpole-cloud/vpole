using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Declarations;
using Pole.Features.Functions.Application.Lifecycle;
using Pole.Features.Functions.Application.FunctionApps;
using Pole.Features.Functions.Application.Invocations;
using Pole.Features.Functions.Application.Revisions;
using Pole.Features.Functions.Application.Usage;
using Pole.Features.Functions.Application.Runtime.Gateway;
using Pole.Features.Functions.Application.Runtime.Metering;
using Pole.Features.Functions.Application.Runtime.Provisioning;
using Pole.Features.Functions.Application.Runtime.Scaling;
using Pole.Features.Functions.Application.Runtime.Timers;
using Pole.Features.Functions.Application;
using Pole.Features.Functions.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.ResultType;
using System.Text.RegularExpressions;

namespace Pole.Features.Functions.Application.Mongo;

internal static class FunctionAppFilters
{
    public static FilterDefinition<FunctionApp> ByName(string tenantId, string name) =>
        ByTenant(tenantId) & Builders<FunctionApp>.Filter.Eq(app => app.NormalizedName, name);

    public static FilterDefinition<FunctionApp> AtPlanningVersion(FunctionApp root) =>
        ByTenantAndId(root.TenantId, root.Id) &
        Builders<FunctionApp>.Filter.Eq(app => app.PlanningFence, root.PlanningFence) &
        Builders<FunctionApp>.Filter.Eq(app => app.NextRevisionNumber, root.NextRevisionNumber) &
        Builders<FunctionApp>.Filter.Eq(app => app.ActiveRevisionId, root.ActiveRevisionId) &
        Builders<FunctionApp>.Filter.Eq(app => app.PendingRevisionId, root.PendingRevisionId) &
        Builders<FunctionApp>.Filter.Eq(app => app.DesiredState, root.DesiredState);

    public static FilterDefinition<FunctionApp> ByTenant(string tenantId) =>
        Builders<FunctionApp>.Filter.Eq(app => app.TenantId, tenantId);

    public static FilterDefinition<FunctionApp> ByTenantAndId(string tenantId, string functionAppId) =>
        Builders<FunctionApp>.Filter.And(
            ByTenant(tenantId),
            Builders<FunctionApp>.Filter.Eq(app => app.Id, functionAppId));

    public static FilterDefinition<FunctionApp> ForList(FunctionAppListQuery query)
    {
        var filter = ByTenant(query.TenantId);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var namePattern = new BsonRegularExpression(
                $".*{Regex.Escape(query.Search)}.*",
                "i");
            var name = Builders<FunctionApp>.Filter.Regex(app => app.Name, namePattern);
            filter &= ObjectId.TryParse(query.Search, out _)
                ? Builders<FunctionApp>.Filter.Or(
                    name,
                    Builders<FunctionApp>.Filter.Eq(app => app.Id, query.Search))
                : name;
        }

        if (!string.IsNullOrWhiteSpace(query.ResourceGroupId))
        {
            filter &= Builders<FunctionApp>.Filter.Eq(
                app => app.ResourceGroupId,
                query.ResourceGroupId);
        }

        return filter;
    }

    public static FilterDefinition<FunctionApp> ForPendingRevision(
        FunctionApp current) =>
        Builders<FunctionApp>.Filter.And(
            ByTenantAndId(current.TenantId, current.Id),
            Builders<FunctionApp>.Filter.Eq(app => app.PendingRevisionId, null),
            Builders<FunctionApp>.Filter.Eq(app => app.PlanningFence, current.PlanningFence),
            Builders<FunctionApp>.Filter.Eq(app => app.DesiredState, current.DesiredState),
            Builders<FunctionApp>.Filter.Eq(
                app => app.NextRevisionNumber,
                current.NextRevisionNumber));

    public static FilterDefinition<FunctionApp> ForDeletion(FunctionApp current) =>
        Builders<FunctionApp>.Filter.And(
            AtPlanningVersion(current),
            Builders<FunctionApp>.Filter.Ne(
                app => app.DesiredState,
                FunctionAppDesiredState.Deleting));

    public static FilterDefinition<FunctionApp> ForRetry(
        FunctionApp current,
        string revisionId) =>
        Builders<FunctionApp>.Filter.And(
            ByTenantAndId(current.TenantId, current.Id),
            Builders<FunctionApp>.Filter.Eq(app => app.ActiveRevisionId, revisionId),
            Builders<FunctionApp>.Filter.Eq(app => app.UpdatedAtUtc, current.UpdatedAtUtc));
}

internal static class FunctionRevisionFilters
{
    public static FilterDefinition<FunctionRevision> AtPlanningVersion(FunctionRevision revision) =>
        ByFunctionApp(revision.TenantId, revision.FunctionAppId) &
        Builders<FunctionRevision>.Filter.Eq(item => item.Id, revision.Id) &
        Builders<FunctionRevision>.Filter.Eq(item => item.PlanningFence, revision.PlanningFence);

    public static FilterDefinition<FunctionRevision> ReferencedConfiguration(
        FunctionApp app,
        IReadOnlyCollection<string> revisionIds) =>
        ByFunctionApp(app.TenantId, app.Id) & Builders<FunctionRevision>.Filter.Or(
            Builders<FunctionRevision>.Filter.In(revision => revision.Id, revisionIds),
            Builders<FunctionRevision>.Filter.Eq(revision => revision.RevisionNumber, app.NextRevisionNumber - 1));

    public static FilterDefinition<FunctionRevision> ByFunctionApp(
        string tenantId,
        string functionAppId) =>
        Builders<FunctionRevision>.Filter.And(
            Builders<FunctionRevision>.Filter.Eq(revision => revision.TenantId, tenantId),
            Builders<FunctionRevision>.Filter.Eq(revision => revision.FunctionAppId, functionAppId));

    public static FilterDefinition<FunctionRevision> ByFunctionAppAndIds(
        string tenantId,
        string functionAppId,
        IReadOnlyCollection<string> revisionIds) =>
        Builders<FunctionRevision>.Filter.And(
            ByFunctionApp(tenantId, functionAppId),
            Builders<FunctionRevision>.Filter.In(revision => revision.Id, revisionIds));
}

internal static class FunctionRouteBindingFilters
{
    public static FilterDefinition<FunctionRouteBinding> ByRoute(string route) =>
        Builders<FunctionRouteBinding>.Filter.Eq(item => item.NormalizedRoute, route);

    public static FilterDefinition<FunctionRouteBinding> AtPlanningVersion(FunctionRouteBinding route) =>
        ByRoute(route.NormalizedRoute) &
        Builders<FunctionRouteBinding>.Filter.Eq(item => item.TenantId, route.TenantId) &
        Builders<FunctionRouteBinding>.Filter.Eq(item => item.FunctionAppId, route.FunctionAppId) &
        Builders<FunctionRouteBinding>.Filter.Eq(item => item.PlanningFence, route.PlanningFence);

    public static FilterDefinition<FunctionRouteBinding> ByFunctionApp(FunctionApp app) =>
        Builders<FunctionRouteBinding>.Filter.And(
            Builders<FunctionRouteBinding>.Filter.Eq(route => route.TenantId, app.TenantId),
            Builders<FunctionRouteBinding>.Filter.Eq(route => route.FunctionAppId, app.Id),
            Builders<FunctionRouteBinding>.Filter.Eq(route => route.NormalizedRoute, app.FunctionRoute));
}

internal static class FunctionTimerFilters
{
    public static FilterDefinition<FunctionTimerState> EnabledByFunctionApp(FunctionApp app) =>
        Builders<FunctionTimerState>.Filter.And(
            Builders<FunctionTimerState>.Filter.Eq(timer => timer.TenantId, app.TenantId),
            Builders<FunctionTimerState>.Filter.Eq(timer => timer.FunctionAppId, app.Id),
            Builders<FunctionTimerState>.Filter.Eq(timer => timer.Enabled, true));

    public static FilterDefinition<FunctionTimerState> ActiveForRetry(
        FunctionApp app,
        FunctionInvocation invocation) =>
        Builders<FunctionTimerState>.Filter.And(
            EnabledByFunctionApp(app),
            Builders<FunctionTimerState>.Filter.Eq(timer => timer.TriggerId, invocation.TriggerId));

    public static FilterDefinition<FunctionTimerState> ForRetryStamp(FunctionTimerState timer) =>
        Builders<FunctionTimerState>.Filter.And(
            Builders<FunctionTimerState>.Filter.Eq(candidate => candidate.Id, timer.Id),
            Builders<FunctionTimerState>.Filter.Eq(
                candidate => candidate.UpdatedAtUtc,
                timer.UpdatedAtUtc));
}

internal static class FunctionInvocationRetryFilters
{
    public static FilterDefinition<FunctionInvocation> ByIdForFunctionApp(
        FunctionApp app,
        string invocationId) =>
        Builders<FunctionInvocation>.Filter.And(
            FunctionInvocationFilters.ByFunctionApp(app.TenantId, app.Id),
            Builders<FunctionInvocation>.Filter.Eq(invocation => invocation.Id, invocationId));

    public static FilterDefinition<FunctionInvocation> Overlapping(
        FunctionApp app,
        FunctionInvocation invocation) =>
        Builders<FunctionInvocation>.Filter.And(
            FunctionInvocationFilters.ByFunctionApp(app.TenantId, app.Id),
            Builders<FunctionInvocation>.Filter.Ne(candidate => candidate.Id, invocation.Id),
            Builders<FunctionInvocation>.Filter.Eq(candidate => candidate.TriggerId, invocation.TriggerId),
            Builders<FunctionInvocation>.Filter.In(
                candidate => candidate.State,
                [
                    FunctionInvocationState.Pending,
                    FunctionInvocationState.Dispatching,
                    FunctionInvocationState.Running,
                    FunctionInvocationState.RetryScheduled
                ]));

    public static FilterDefinition<FunctionInvocation> ForManualRetry(
        FunctionApp app,
        FunctionInvocation invocation) =>
        Builders<FunctionInvocation>.Filter.And(
            ByIdForFunctionApp(app, invocation.Id),
            Builders<FunctionInvocation>.Filter.Eq(
                candidate => candidate.State,
                FunctionInvocationState.Failed),
            Builders<FunctionInvocation>.Filter.Eq(
                candidate => candidate.UpdatedAtUtc,
                invocation.UpdatedAtUtc));
}

internal static class FunctionInvocationFilters
{
    public static FilterDefinition<FunctionInvocation> ByFunctionApp(
        string tenantId,
        string functionAppId) =>
        Builders<FunctionInvocation>.Filter.And(
            Builders<FunctionInvocation>.Filter.Eq(invocation => invocation.TenantId, tenantId),
            Builders<FunctionInvocation>.Filter.Eq(invocation => invocation.FunctionAppId, functionAppId));
}

internal static class FunctionUsageFilters
{
    public static FilterDefinition<FunctionUsageBucket> ForRange(
        FunctionApp app,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc)
    {
        var filter = Builders<FunctionUsageBucket>.Filter.And(
            Builders<FunctionUsageBucket>.Filter.Eq(bucket => bucket.TenantId, app.TenantId),
            Builders<FunctionUsageBucket>.Filter.Eq(bucket => bucket.FunctionAppId, app.Id));
        if (fromUtc is { } from)
            filter &= Builders<FunctionUsageBucket>.Filter.Gte(bucket => bucket.HourUtc, from);
        if (toUtc is { } to)
            filter &= Builders<FunctionUsageBucket>.Filter.Lte(bucket => bucket.HourUtc, to);
        return filter;
    }

    public static FilterDefinition<FunctionMeterEvent> AggregatedFor(FunctionApp app) =>
        Builders<FunctionMeterEvent>.Filter.And(
            Builders<FunctionMeterEvent>.Filter.Eq(meter => meter.TenantId, app.TenantId),
            Builders<FunctionMeterEvent>.Filter.Eq(meter => meter.FunctionAppId, app.Id),
            Builders<FunctionMeterEvent>.Filter.Ne(meter => meter.AggregatedAtUtc, null));
}

internal static class FunctionAppDeletionFilters
{
    public static FilterDefinition<FunctionApp> App(
        string tenantId,
        string functionAppId,
        string resourceDeploymentId) =>
        Builders<FunctionApp>.Filter.And(
            FunctionAppFilters.ByTenantAndId(tenantId, functionAppId),
            Builders<FunctionApp>.Filter.Eq(
                app => app.DesiredState,
                FunctionAppDesiredState.Deleting),
            Builders<FunctionApp>.Filter.Eq(
                app => app.Placement.ResourceDeploymentId,
                resourceDeploymentId));

    public static FilterDefinition<FunctionRevision> Revisions(
        string tenantId,
        string functionAppId) =>
        FunctionRevisionFilters.ByFunctionApp(tenantId, functionAppId);

    public static FilterDefinition<FunctionRouteBinding> Routes(
        string tenantId,
        string functionAppId) =>
        Builders<FunctionRouteBinding>.Filter.Eq(item => item.TenantId, tenantId) &
        Builders<FunctionRouteBinding>.Filter.Eq(item => item.FunctionAppId, functionAppId);

    public static FilterDefinition<FunctionTimerState> Timers(
        string tenantId,
        string functionAppId) =>
        Builders<FunctionTimerState>.Filter.Eq(item => item.TenantId, tenantId) &
        Builders<FunctionTimerState>.Filter.Eq(item => item.FunctionAppId, functionAppId);

    public static FilterDefinition<FunctionInvocation> Invocations(
        string tenantId,
        string functionAppId) =>
        Builders<FunctionInvocation>.Filter.Eq(item => item.TenantId, tenantId) &
        Builders<FunctionInvocation>.Filter.Eq(item => item.FunctionAppId, functionAppId);

    public static FilterDefinition<FunctionMeterEvent> MeterEvents(
        string tenantId,
        string functionAppId) =>
        Builders<FunctionMeterEvent>.Filter.Eq(item => item.TenantId, tenantId) &
        Builders<FunctionMeterEvent>.Filter.Eq(item => item.FunctionAppId, functionAppId);

    public static FilterDefinition<FunctionUsageBucket> UsageBuckets(
        string tenantId,
        string functionAppId) =>
        Builders<FunctionUsageBucket>.Filter.Eq(item => item.TenantId, tenantId) &
        Builders<FunctionUsageBucket>.Filter.Eq(item => item.FunctionAppId, functionAppId);

}

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

internal static class FunctionRuntimeRouteTargetFilters
{
    public static FilterDefinition<FunctionRouteBinding> ActiveBinding(string normalizedRoute) =>
        Builders<FunctionRouteBinding>.Filter.And(
            Builders<FunctionRouteBinding>.Filter.Eq(binding => binding.NormalizedRoute, normalizedRoute),
            Builders<FunctionRouteBinding>.Filter.Eq(binding => binding.State, FunctionRouteBindingState.Active),
            Builders<FunctionRouteBinding>.Filter.Ne(binding => binding.ActiveRevisionId, null));

    public static FilterDefinition<FunctionApp> ServingApp(
        FunctionRouteBinding binding,
        string revisionId) =>
        Builders<FunctionApp>.Filter.And(
            Builders<FunctionApp>.Filter.Eq(app => app.TenantId, binding.TenantId),
            Builders<FunctionApp>.Filter.Eq(app => app.Id, binding.FunctionAppId),
            Builders<FunctionApp>.Filter.Eq(app => app.FunctionRoute, binding.NormalizedRoute),
            Builders<FunctionApp>.Filter.Eq(app => app.ActiveRevisionId, revisionId),
            Builders<FunctionApp>.Filter.Eq(app => app.DesiredState, FunctionAppDesiredState.Enabled),
            Builders<FunctionApp>.Filter.Eq(app => app.Status, FunctionAppStatus.Ready));

    public static FilterDefinition<FunctionRevision> ActiveRevision(
        FunctionRouteBinding binding,
        string revisionId) =>
        Builders<FunctionRevision>.Filter.And(
            Builders<FunctionRevision>.Filter.Eq(revision => revision.TenantId, binding.TenantId),
            Builders<FunctionRevision>.Filter.Eq(revision => revision.FunctionAppId, binding.FunctionAppId),
            Builders<FunctionRevision>.Filter.Eq(revision => revision.Id, revisionId),
            Builders<FunctionRevision>.Filter
                .OfType<FunctionRevisionStatus, ActiveFunctionRevisionStatus>(revision => revision.Status));
}

internal static class FunctionMeterAggregationFilters
{
    public static FilterDefinition<FunctionMeterEvent> Unaggregated() =>
        Builders<FunctionMeterEvent>.Filter.Eq(meter => meter.AggregatedAtUtc, null);

    public static FilterDefinition<FunctionMeterEvent> UnaggregatedById(string meterEventId) =>
        Builders<FunctionMeterEvent>.Filter.And(
            Builders<FunctionMeterEvent>.Filter.Eq(meter => meter.Id, meterEventId),
            Unaggregated());

    public static FilterDefinition<FunctionUsageBucket> ById(string bucketId) =>
        Builders<FunctionUsageBucket>.Filter.Eq(bucket => bucket.Id, bucketId);
}

internal static class FunctionRevisionProvisioningSubjectFilters
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

internal static class FunctionInvocationClaimFilters
{
    public static FilterDefinition<FunctionInvocation> ReadyAt(DateTimeOffset nowUtc) =>
        Builders<FunctionInvocation>.Filter.Or(
            Builders<FunctionInvocation>.Filter.And(
                Builders<FunctionInvocation>.Filter.Eq(
                    invocation => invocation.State,
                    FunctionInvocationState.Pending),
                Builders<FunctionInvocation>.Filter.Or(
                    Builders<FunctionInvocation>.Filter.Eq(invocation => invocation.NextAttemptAtUtc, null),
                    Builders<FunctionInvocation>.Filter.Lte(invocation => invocation.NextAttemptAtUtc, nowUtc))),
            Builders<FunctionInvocation>.Filter.And(
                Builders<FunctionInvocation>.Filter.Eq(
                    invocation => invocation.State,
                    FunctionInvocationState.RetryScheduled),
                Builders<FunctionInvocation>.Filter.Lte(invocation => invocation.NextAttemptAtUtc, nowUtc)));

    public static FilterDefinition<FunctionInvocation> CurrentCandidate(FunctionInvocation invocation) =>
        Builders<FunctionInvocation>.Filter.And(
            Builders<FunctionInvocation>.Filter.Eq(candidate => candidate.Id, invocation.Id),
            Builders<FunctionInvocation>.Filter.Eq(candidate => candidate.State, invocation.State),
            Builders<FunctionInvocation>.Filter.Eq(candidate => candidate.AttemptNumber, invocation.AttemptNumber),
            Builders<FunctionInvocation>.Filter.Eq(candidate => candidate.UpdatedAtUtc, invocation.UpdatedAtUtc),
            Builders<FunctionInvocation>.Filter.Eq(candidate => candidate.Lease, null));
}

internal static class FunctionTimerSchedulerFilters
{
    public static FilterDefinition<FunctionTimerState> ClaimableAt(DateTimeOffset nowUtc) =>
        Builders<FunctionTimerState>.Filter.And(
            Builders<FunctionTimerState>.Filter.Eq(timer => timer.Enabled, true),
            Builders<FunctionTimerState>.Filter.Lte(timer => timer.NextOccurrenceUtc, nowUtc),
            Builders<FunctionTimerState>.Filter.Or(
                Builders<FunctionTimerState>.Filter.Eq(timer => timer.Lease, null),
                Builders<FunctionTimerState>.Filter.Lte(item => item.Lease!.ExpiresAtUtc, nowUtc)));
}

internal static class FunctionInvocationDispatchFilters
{
    public static FilterDefinition<FunctionInvocation> ExpiredAttemptAt(DateTimeOffset nowUtc) =>
        Builders<FunctionInvocation>.Filter.And(
            Builders<FunctionInvocation>.Filter.In(
                invocation => invocation.State,
                [FunctionInvocationState.Dispatching, FunctionInvocationState.Running]),
            Builders<FunctionInvocation>.Filter.Ne(invocation => invocation.Lease, null),
            Builders<FunctionInvocation>.Filter.Lte(item => item.Lease!.ExpiresAtUtc, nowUtc));
}

internal static class FunctionInvocationDispatchSubjectFilters
{
    public static FilterDefinition<FunctionApp> EnabledApp(FunctionInvocation invocation) =>
        Builders<FunctionApp>.Filter.And(
            Builders<FunctionApp>.Filter.Eq(app => app.TenantId, invocation.TenantId),
            Builders<FunctionApp>.Filter.Eq(app => app.Id, invocation.FunctionAppId),
            Builders<FunctionApp>.Filter.Eq(app => app.DesiredState, FunctionAppDesiredState.Enabled));

    public static FilterDefinition<FunctionRevision> DispatchableRevision(FunctionInvocation invocation) =>
        Builders<FunctionRevision>.Filter.And(
            Builders<FunctionRevision>.Filter.Eq(revision => revision.TenantId, invocation.TenantId),
            Builders<FunctionRevision>.Filter.Eq(revision => revision.FunctionAppId, invocation.FunctionAppId),
            Builders<FunctionRevision>.Filter.Eq(revision => revision.Id, invocation.RevisionId),
            Builders<FunctionRevision>.Filter.Or(
                Builders<FunctionRevision>.Filter
                    .OfType<FunctionRevisionStatus, ActiveFunctionRevisionStatus>(revision => revision.Status),
                Builders<FunctionRevision>.Filter
                    .OfType<FunctionRevisionStatus, DrainingFunctionRevisionStatus>(revision => revision.Status)));
}

internal static class FunctionTimerOccurrenceFilters
{
    public static FilterDefinition<FunctionTimerState> ClaimedTimer(FunctionTimerState claimed) =>
        Builders<FunctionTimerState>.Filter.And(
            Builders<FunctionTimerState>.Filter.Eq(timer => timer.Id, claimed.Id),
            Builders<FunctionTimerState>.Filter.Eq(timer => timer.TenantId, claimed.TenantId),
            Builders<FunctionTimerState>.Filter.Eq(timer => timer.FunctionAppId, claimed.FunctionAppId),
            Builders<FunctionTimerState>.Filter.Eq(timer => timer.Enabled, true),
            Builders<FunctionTimerState>.Filter.Ne(timer => timer.Lease, null),
            Builders<FunctionTimerState>.Filter.Eq(item => item.Lease!.Token, claimed.Lease!.Token));

    public static FilterDefinition<FunctionInvocation> InvocationById(string invocationId) =>
        Builders<FunctionInvocation>.Filter.Eq(invocation => invocation.Id, invocationId);

    public static FilterDefinition<FunctionInvocation> ActiveSiblingInvocation(
        FunctionTimerState timer,
        string invocationId) =>
        Builders<FunctionInvocation>.Filter.And(
            Builders<FunctionInvocation>.Filter.Eq(candidate => candidate.TenantId, timer.TenantId),
            Builders<FunctionInvocation>.Filter.Eq(candidate => candidate.TriggerId, timer.TriggerId),
            Builders<FunctionInvocation>.Filter.Ne(candidate => candidate.Id, invocationId),
            Builders<FunctionInvocation>.Filter.In(
                candidate => candidate.State,
                [
                    FunctionInvocationState.Pending,
                    FunctionInvocationState.Dispatching,
                    FunctionInvocationState.Running,
                    FunctionInvocationState.RetryScheduled
                ]));

    public static FilterDefinition<FunctionTimerState> Advance(
        FunctionTimerState timer,
        DateTimeOffset scheduledForUtc) =>
        Builders<FunctionTimerState>.Filter.And(
            Builders<FunctionTimerState>.Filter.Eq(candidate => candidate.Id, timer.Id),
            Builders<FunctionTimerState>.Filter.Ne(candidate => candidate.Lease, null),
            Builders<FunctionTimerState>.Filter.Eq(item => item.Lease!.Token, timer.Lease!.Token),
            Builders<FunctionTimerState>.Filter.Eq(candidate => candidate.NextOccurrenceUtc, scheduledForUtc));
}

internal static class FunctionInvocationTransitionFilters
{
    public static FilterDefinition<FunctionInvocation> CurrentLeasedAttempt(FunctionInvocation invocation) =>
        Builders<FunctionInvocation>.Filter.And(
            Builders<FunctionInvocation>.Filter.Eq(candidate => candidate.Id, invocation.Id),
            Builders<FunctionInvocation>.Filter.Eq(candidate => candidate.State, invocation.State),
            Builders<FunctionInvocation>.Filter.Eq(candidate => candidate.AttemptNumber, invocation.AttemptNumber),
            Builders<FunctionInvocation>.Filter.Ne(candidate => candidate.Lease, null),
            Builders<FunctionInvocation>.Filter.Eq(item => item.Lease!.Token, invocation.Lease!.Token));
}

internal static class FunctionTimerLeaseFilters
{
    public static FilterDefinition<FunctionTimerState> OwnedBy(FunctionTimerState timer) =>
        Builders<FunctionTimerState>.Filter.And(
            Builders<FunctionTimerState>.Filter.Eq(candidate => candidate.Id, timer.Id),
            Builders<FunctionTimerState>.Filter.Ne(candidate => candidate.Lease, null),
            Builders<FunctionTimerState>.Filter.Eq(item => item.Lease!.Token, timer.Lease!.Token));
}
