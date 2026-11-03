using MongoDB.Driver;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Persistence.Runtime.Timers;

public delegate Task ReleaseFunctionTimerLease(
    FunctionTimerState timer,
    CancellationToken cancellationToken = default);

public sealed class MongoReleaseFunctionTimerLease(
    IMongoCollection<FunctionTimerState> functionTimerStates,
    TimeProvider timeProvider)
{
    public Task InvokeAsync(
        FunctionTimerState timer,
        CancellationToken cancellationToken = default)
    {
        if (timer.Lease is null)
            return Task.CompletedTask;

        var now = timeProvider.GetUtcNow();
        return functionTimerStates.UpdateOneAsync(
            FunctionTimerLeaseFilters.OwnedBy(timer),
            Builders<FunctionTimerState>.Update
                .Set(candidate => candidate.Lease, null)
                .Set(candidate => candidate.UpdatedAtUtc, now),
            cancellationToken: cancellationToken);
    }
}

internal static class FunctionTimerLeaseFilters
{
    public static FilterDefinition<FunctionTimerState> OwnedBy(FunctionTimerState timer) =>
        Builders<FunctionTimerState>.Filter.And(
            Builders<FunctionTimerState>.Filter.Eq(candidate => candidate.Id, timer.Id),
            Builders<FunctionTimerState>.Filter.Ne(candidate => candidate.Lease, null),
            Builders<FunctionTimerState>.Filter.Eq(item => item.Lease!.Token, timer.Lease!.Token));
}
