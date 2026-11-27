// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Domain;
using Pole.Features.Quotas.Contracts;

namespace Pole.Features.Functions.Application.Lifecycle;

public delegate Task<FunctionRevisionLifecycleOutcome> FailFunctionRevision(
    string tenantId,
    string functionAppId,
    string revisionId,
    FunctionRevisionFailure failure,
    CancellationToken cancellationToken = default);

internal sealed class MongoFailFunctionRevision(
    IMongoCollection<FunctionApp> functionApps,
    IMongoCollection<FunctionRevision> functionRevisions,
    StoreOutboxWrites storeOutboxWrites,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    MongoFunctionRevisionLifecycleStore lifecycleStore,
    TimeProvider timeProvider
)
{
    public async Task<FunctionRevisionLifecycleOutcome> InvokeAsync(
        string tenantId,
        string functionAppId,
        string revisionId,
        FunctionRevisionFailure failure,
        CancellationToken cancellationToken = default)
    {
        var loaded = await lifecycleStore.LoadAsync(tenantId, functionAppId, revisionId, cancellationToken);
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

        if (state.App.PendingRevisionId is null && state.Revision.Status is FailedFunctionRevisionStatus failed &&
            failed.Failure == failure)
            return new FunctionRevisionLifecycleCompleted();
        if (state.App.PendingRevisionId != revisionId)
            return new FunctionRevisionNotPending(functionAppId, revisionId);
        if (state.App.DesiredState != FunctionAppDesiredState.Enabled)
            return new FunctionAppIsNotEnabled(functionAppId);

        var targetAllocation = state.ActiveRevision is null
            ? new FunctionQuotaAllocation(1, 0, 0, 0)
            : FunctionQuotaAllocation.FromRevision(state.ActiveRevision.Spec);
        return await
            quotaAdmissionExecutor.ExecuteOutcomeAsync<FunctionQuotaAllocation, FunctionRevisionLifecycleOutcome>(
                new ExecuteQuotaAdmissionRequest<FunctionQuotaAllocation, FunctionRevisionLifecycleOutcome>(
                    TenantId: tenantId,
                    ResourceKey: FunctionAppQuotaResourceKey.Create(functionAppId),
                    Mutation: new UpdateQuotaMutation<FunctionQuotaAllocation>(
                        state.App.AdmittedQuota,
                        targetAllocation),
                    QuotaError: quotaError => quotaError,
                    MutationCallback: async (transaction, transactionCancellationToken) =>
                    {
                        var now = timeProvider.GetUtcNow();
                        var failedRevision = state.Revision.Fail(failure);
                        var revisionUpdate = await functionRevisions.UpdateOneAsync(
                            transaction.Session,
                            FunctionRevisionLifecycleFilters.RevisionInOneOfStatuses(
                                tenantId,
                                functionAppId,
                                revisionId,
                                FunctionRevisionLifecycleFilters.ProvisioningStatus(),
                                FunctionRevisionLifecycleFilters.ReadyStatus()),
                            Builders<FunctionRevision>.Update.Set(revision => revision.Status, failedRevision.Status),
                            cancellationToken: transactionCancellationToken);
                        if (revisionUpdate.MatchedCount != 1)
                            return transaction.Abort(
                                new FunctionRevisionConcurrencyConflict(functionAppId, revisionId));

                        var appUpdate = await functionApps.UpdateOneAsync(
                            transaction.Session,
                            FunctionRevisionLifecycleFilters.PendingApp(tenantId, functionAppId, revisionId),
                            Builders<FunctionApp>.Update
                                .Set(app => app.PendingRevisionId, null)
                                .Set(
                                    app => app.Status,
                                    state.App.ActiveRevisionId is null
                                        ? FunctionAppStatus.Failed
                                        : FunctionAppStatus.Ready)
                                .Set(app => app.AdmittedQuota, targetAllocation)
                                .Set(app => app.UpdatedAtUtc, now),
                            cancellationToken: transactionCancellationToken);
                        if (appUpdate.MatchedCount != 1)
                            return transaction.Abort(new FunctionAppConcurrencyConflict(functionAppId));

                        await storeOutboxWrites(
                            transaction.Session,
                            [OutboxWrite.Create(state.Revision.ToFailedEvent(state.App, failure))],
                            transactionCancellationToken);
                        return transaction.Commit(new FunctionRevisionLifecycleCompleted());
                    }),
                cancellationToken: cancellationToken);
    }
}
