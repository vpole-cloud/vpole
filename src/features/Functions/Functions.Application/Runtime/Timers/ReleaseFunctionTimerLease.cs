using MongoDB.Driver;
using Pole.Features.Functions.Application;
using Pole.Features.Functions.Application.Declarations;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Runtime.Timers;

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
