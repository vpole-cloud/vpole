using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Application.Persistence.Reads;

namespace Pole.Features.Functions.Application.Persistence;

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
