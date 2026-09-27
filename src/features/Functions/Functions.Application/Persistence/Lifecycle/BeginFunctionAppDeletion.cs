using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Persistence;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Events;

namespace Pole.Features.Functions.Application.Persistence.Lifecycle;

public sealed record FunctionAppDeletionChanged(string FunctionAppId);

public union BeginFunctionAppDeletionOutcome(
    FunctionApp,
    FunctionAppDeletionChanged);

public delegate Task<BeginFunctionAppDeletionOutcome>
    BeginFunctionAppDeletion(
        IClientSessionHandle session,
        FunctionApp current,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

internal sealed class MongoBeginFunctionAppDeletion(
    IMongoCollection<FunctionApp> functionApps,
    IMongoCollection<FunctionRouteBinding> functionRouteBindings,
    IMongoCollection<FunctionTimerState> functionTimerStates,
    StoreOutboxWrites storeOutboxWrites)
{
    public async Task<BeginFunctionAppDeletionOutcome> InvokeAsync(
        IClientSessionHandle session,
        FunctionApp current,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Function App deletion requires an active transaction.");
        var appResult = await functionApps.UpdateOneAsync(
            session,
            FunctionAppFilters.ForDeletion(current),
            Builders<FunctionApp>.Update
                .Set(app => app.DesiredState, FunctionAppDesiredState.Deleting)
                .Set(app => app.Status, FunctionAppStatus.Deleting)
                .Set(app => app.UpdatedAtUtc, nowUtc),
            cancellationToken: cancellationToken);
        if (appResult.MatchedCount != 1)
        {
            return new FunctionAppDeletionChanged(current.Id);
        }

        await functionRouteBindings.UpdateOneAsync(
            session,
            FunctionRouteBindingFilters.ByFunctionApp(current),
            Builders<FunctionRouteBinding>.Update
                .Set(route => route.State, FunctionRouteBindingState.Disabled)
                .Set(route => route.UpdatedAtUtc, nowUtc),
            cancellationToken: cancellationToken);
        await functionTimerStates.UpdateManyAsync(
            session,
            FunctionTimerFilters.EnabledByFunctionApp(current),
            Builders<FunctionTimerState>.Update
                .Set(timer => timer.Enabled, false)
                .Set(timer => timer.Lease, null)
                .Set(timer => timer.UpdatedAtUtc, nowUtc),
            cancellationToken: cancellationToken);
        await storeOutboxWrites(
            session,
            [OutboxWrite.Create(
                new FunctionAppDeleteRequestedEvent(
                    current.Id,
                    current.Placement.ToSnapshot(),
                    current.ActiveRevisionId)
                {
                    EventCore = EventCore.Create(current.TenantId)
                })],
            cancellationToken);
        return current;
    }
}
