using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.Functions.Application.Persistence;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Events;
using Pole.ResultType;

namespace Pole.Features.Functions.Application.Persistence.Lifecycle;

public sealed record FunctionAppDeletionChanged(string FunctionAppId);

public union BeginFunctionAppDeletionError(
    FunctionAppDeletionChanged);

public delegate Task<Result<FunctionApp, BeginFunctionAppDeletionError>>
    BeginFunctionAppDeletion(
        FunctionApp current,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

internal sealed class MongoBeginFunctionAppDeletion(
    IMongoCollection<FunctionApp> functionApps,
    IMongoCollection<FunctionRouteBinding> functionRouteBindings,
    IMongoCollection<FunctionTimerState> functionTimerStates,
    StoreOutboxWrites storeOutboxWrites,
    RunMongoOutcomeTransaction<Result<FunctionApp, BeginFunctionAppDeletionError>> runTransaction)
{
    public Task<Result<FunctionApp, BeginFunctionAppDeletionError>> InvokeAsync(
        FunctionApp current,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        runTransaction(
            (transaction, ct) => BeginAsync(transaction, current, nowUtc, ct),
            cancellationToken);

    private async Task<MongoTransactionDecision<Result<FunctionApp, BeginFunctionAppDeletionError>>>
        BeginAsync(
            MongoTransactionContext<Result<FunctionApp, BeginFunctionAppDeletionError>> transaction,
            FunctionApp current,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken)
    {
        var appResult = await functionApps.UpdateOneAsync(
            transaction.Session,
            FunctionAppFilters.ForDeletion(current),
            Builders<FunctionApp>.Update
                .Set(app => app.DesiredState, FunctionAppDesiredState.Deleting)
                .Set(app => app.Status, FunctionAppStatus.Deleting)
                .Set(app => app.UpdatedAtUtc, nowUtc),
            cancellationToken: cancellationToken);
        if (appResult.MatchedCount != 1)
        {
            return transaction.Abort(new Err<BeginFunctionAppDeletionError>(
                new FunctionAppDeletionChanged(current.Id)));
        }

        await functionRouteBindings.UpdateOneAsync(
            transaction.Session,
            FunctionRouteBindingFilters.ByFunctionApp(current),
            Builders<FunctionRouteBinding>.Update
                .Set(route => route.State, FunctionRouteBindingState.Disabled)
                .Set(route => route.UpdatedAtUtc, nowUtc),
            cancellationToken: cancellationToken);
        await functionTimerStates.UpdateManyAsync(
            transaction.Session,
            FunctionTimerFilters.EnabledByFunctionApp(current),
            Builders<FunctionTimerState>.Update
                .Set(timer => timer.Enabled, false)
                .Set(timer => timer.Lease, null)
                .Set(timer => timer.UpdatedAtUtc, nowUtc),
            cancellationToken: cancellationToken);
        await storeOutboxWrites(
            transaction.Session,
            [OutboxWrite.Create(
                new FunctionAppDeleteRequestedEvent(current.Id, current.ActiveRevisionId)
                {
                    EventCore = EventCore.Create(current.TenantId)
                })],
            cancellationToken);
        return transaction.Commit(new Ok<FunctionApp>(current));
    }
}
