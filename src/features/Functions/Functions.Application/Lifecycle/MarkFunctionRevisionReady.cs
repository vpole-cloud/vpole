// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Lifecycle;

public delegate Task<FunctionRevisionLifecycleOutcome> MarkFunctionRevisionReady(
    string tenantId,
    string functionAppId,
    string revisionId,
    CancellationToken cancellationToken = default);

internal sealed class MongoMarkFunctionRevisionReady(
    IMongoCollection<FunctionRevision> functionRevisions,
    MongoFunctionRevisionLifecycleStore lifecycleStore,
    TimeProvider timeProvider
)
{
    public async Task<FunctionRevisionLifecycleOutcome> InvokeAsync(
        string tenantId,
        string functionAppId,
        string revisionId,
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

        if (state.Revision.Status is ReadyFunctionRevisionStatus)
            return new FunctionRevisionLifecycleCompleted();
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
            return new FunctionRevisionLifecycleCompleted();

        var current = await functionRevisions.Find(
                FunctionRevisionLifecycleFilters.RevisionByOwner(tenantId, functionAppId, revisionId))
            .FirstOrDefaultAsync(cancellationToken);
        return current?.Status is ReadyFunctionRevisionStatus or ActiveFunctionRevisionStatus
            ? new FunctionRevisionLifecycleCompleted()
            : new FunctionRevisionConcurrencyConflict(functionAppId, revisionId);
    }
}
