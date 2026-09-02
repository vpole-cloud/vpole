using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.ResultType;

namespace Pole.Features.Functions.Application.Persistence.Lifecycle;

public sealed record FunctionAppDeletionFinalizationConflict(string FunctionAppId);

public delegate Task<Result<Unit, FunctionAppDeletionFinalizationConflict>>
    FinalizeFunctionAppDeletion(
        string tenantId,
        string functionAppId,
        string resourceDeploymentId,
        CancellationToken cancellationToken = default);

internal sealed class MongoFinalizeFunctionAppDeletion(
    IMongoCollection<FunctionApp> functionApps,
    IMongoCollection<FunctionRevision> functionRevisions,
    IMongoCollection<FunctionRouteBinding> functionRouteBindings,
    IMongoCollection<FunctionTimerState> functionTimerStates,
    IMongoCollection<FunctionInvocation> functionInvocations,
    IMongoCollection<FunctionMeterEvent> functionMeterEvents,
    IMongoCollection<FunctionUsageBucket> functionUsageBuckets,
    GetExactResourceDeploymentFinalizationReadiness getDeploymentReadiness,
    PurgeExactDeletedResourceDeployment purgeDeployment,
    UnregisterResource unregisterResource,
    RunMongoOutcomeTransaction<Result<Unit, FunctionAppDeletionFinalizationConflict>>
        runMongoTransaction)
{
    public Task<Result<Unit, FunctionAppDeletionFinalizationConflict>> InvokeAsync(
        string tenantId,
        string functionAppId,
        string resourceDeploymentId,
        CancellationToken cancellationToken = default) =>
        runMongoTransaction(async (transaction, ct) =>
        {
            var appFilter = FunctionAppDeletionFilters.App(
                tenantId,
                functionAppId,
                resourceDeploymentId);
            var app = await functionApps
                .Find(transaction.Session, appFilter)
                .FirstOrDefaultAsync(ct);
            if (app is null)
                return transaction.Commit(new Ok<Unit>(Unit.Value));

            var readiness = await getDeploymentReadiness(
                tenantId,
                FunctionResourceDeployment.ResourceType,
                functionAppId,
                resourceDeploymentId,
                transaction.Session,
                ct);
            if (readiness is not ResourceDeploymentFinalizationReadiness.Ready)
                return transaction.Commit(new Ok<Unit>(Unit.Value));

            await DeleteOwnedDocumentsAsync(
                transaction.Session,
                tenantId,
                functionAppId,
                ct);
            var deleted = await functionApps.DeleteOneAsync(
                transaction.Session,
                filter: appFilter,
                cancellationToken: ct);
            if (deleted.DeletedCount != 1)
                return transaction.Abort(Conflict(functionAppId));

            var registration = await unregisterResource(
                tenantId,
                app.ResourceGroupId,
                FunctionResourceDeployment.ResourceType,
                functionAppId,
                transaction.Session,
                ct);
            if (!registration.TryGetValue(out _, out _))
                return transaction.Abort(Conflict(functionAppId));

            var purge = await purgeDeployment(
                tenantId,
                FunctionResourceDeployment.ResourceType,
                functionAppId,
                resourceDeploymentId,
                transaction.Session,
                ct);
            if (purge is Err<ResourceDeploymentFinalizationConflict>)
                return transaction.Abort(Conflict(functionAppId));

            return transaction.Commit(new Ok<Unit>(Unit.Value));
        }, cancellationToken);

    private async Task DeleteOwnedDocumentsAsync(
        IClientSessionHandle session,
        string tenantId,
        string functionAppId,
        CancellationToken cancellationToken)
    {
        await functionRevisions.DeleteManyAsync(
            session,
            FunctionAppDeletionFilters.Revisions(tenantId, functionAppId),
            cancellationToken: cancellationToken);
        await functionRouteBindings.DeleteManyAsync(
            session,
            FunctionAppDeletionFilters.Routes(tenantId, functionAppId),
            cancellationToken: cancellationToken);
        await functionTimerStates.DeleteManyAsync(
            session,
            FunctionAppDeletionFilters.Timers(tenantId, functionAppId),
            cancellationToken: cancellationToken);
        await functionInvocations.DeleteManyAsync(
            session,
            FunctionAppDeletionFilters.Invocations(tenantId, functionAppId),
            cancellationToken: cancellationToken);
        await functionMeterEvents.DeleteManyAsync(
            session,
            FunctionAppDeletionFilters.MeterEvents(tenantId, functionAppId),
            cancellationToken: cancellationToken);
        await functionUsageBuckets.DeleteManyAsync(
            session,
            FunctionAppDeletionFilters.UsageBuckets(tenantId, functionAppId),
            cancellationToken: cancellationToken);
    }

    private static Err<FunctionAppDeletionFinalizationConflict> Conflict(
        string functionAppId) =>
        new(new FunctionAppDeletionFinalizationConflict(functionAppId));
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
        Owned<FunctionRouteBinding>(tenantId, functionAppId);

    public static FilterDefinition<FunctionTimerState> Timers(
        string tenantId,
        string functionAppId) =>
        Owned<FunctionTimerState>(tenantId, functionAppId);

    public static FilterDefinition<FunctionInvocation> Invocations(
        string tenantId,
        string functionAppId) =>
        Owned<FunctionInvocation>(tenantId, functionAppId);

    public static FilterDefinition<FunctionMeterEvent> MeterEvents(
        string tenantId,
        string functionAppId) =>
        Owned<FunctionMeterEvent>(tenantId, functionAppId);

    public static FilterDefinition<FunctionUsageBucket> UsageBuckets(
        string tenantId,
        string functionAppId) =>
        Owned<FunctionUsageBucket>(tenantId, functionAppId);

    private static FilterDefinition<T> Owned<T>(
        string tenantId,
        string functionAppId) =>
        Builders<T>.Filter.And(
            Builders<T>.Filter.Eq("TenantId", tenantId),
            Builders<T>.Filter.Eq("FunctionAppId", functionAppId));
}
