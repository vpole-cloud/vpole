// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;

namespace Pole.Features.Functions.Application.Lifecycle;

public sealed record FunctionAppDeletionFinalizationConflict(
    string FunctionAppId
);

public sealed record FunctionAppDeletionFinalized;

public union FinalizeFunctionAppDeletionOutcome(FunctionAppDeletionFinalized, FunctionAppDeletionFinalizationConflict);

public delegate Task<FinalizeFunctionAppDeletionOutcome> FinalizeFunctionAppDeletion(
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
    IResourceRegistrar resourceRegistrar,
    RunMongoOutcomeTransaction<FinalizeFunctionAppDeletionOutcome> runMongoTransaction
)
{
    public Task<FinalizeFunctionAppDeletionOutcome> InvokeAsync(
        string tenantId,
        string functionAppId,
        string resourceDeploymentId,
        CancellationToken cancellationToken = default) => runMongoTransaction(
        async (transaction, ct) =>
        {
            var appFilter = FunctionAppDeletionFilters.App(tenantId, functionAppId, resourceDeploymentId);
            var app = await functionApps.Find(transaction.Session, appFilter).FirstOrDefaultAsync(ct);
            if (app is null)
                return transaction.Commit(Finalized());

            var readiness = await getDeploymentReadiness(
                new GetExactResourceDeploymentFinalizationReadinessRequest(
                    TenantId: tenantId,
                    ResourceType: FunctionResourceDeployment.ResourceType,
                    ResourceId: functionAppId,
                    ResourceDeploymentId: resourceDeploymentId),
                session: transaction.Session,
                cancellationToken: ct);
            if (readiness is not ResourceDeploymentFinalizationReadiness.Ready)
                return transaction.Commit(Finalized());

            await DeleteOwnedDocumentsAsync(transaction.Session, tenantId, functionAppId, ct);
            var deleted = await functionApps.DeleteOneAsync(
                transaction.Session,
                filter: appFilter,
                cancellationToken: ct);
            if (deleted.DeletedCount != 1)
                return transaction.Abort(Conflict(functionAppId));

            var registration = await resourceRegistrar.UnregisterAsync(
                new UnregisterResourceRequest(
                    TenantId: tenantId,
                    ResourceGroupId: app.ResourceGroupId,
                    ResourceTypeId: FunctionResourceDeployment.ResourceType,
                    ResourceId: functionAppId),
                session: transaction.Session,
                cancellationToken: ct);
            if (registration is ResourceRegistrationMutationError)
                return transaction.Abort(Conflict(functionAppId));

            var purge = await purgeDeployment(
                new PurgeExactDeletedResourceDeploymentRequest(
                    TenantId: tenantId,
                    ResourceType: FunctionResourceDeployment.ResourceType,
                    ResourceId: functionAppId,
                    ResourceDeploymentId: resourceDeploymentId),
                session: transaction.Session,
                cancellationToken: ct);
            if (purge is ResourceDeploymentFinalizationConflict)
                return transaction.Abort(Conflict(functionAppId));

            return transaction.Commit(Finalized());
        },
        cancellationToken);

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

    private static FinalizeFunctionAppDeletionOutcome Finalized() => new FunctionAppDeletionFinalized();

    private static FinalizeFunctionAppDeletionOutcome Conflict(string functionAppId) =>
        new FunctionAppDeletionFinalizationConflict(functionAppId);
}
