using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Application.Declarations;
using Pole.Features.Functions.Application;
using MongoDB.Driver;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Runtime.Timers;

public delegate Task<FunctionTimerState?> ClaimNextFunctionTimer(
    string ownerId,
    int leaseSeconds,
    CancellationToken cancellationToken = default);

public sealed class MongoClaimNextFunctionTimer(
    IMongoCollection<FunctionTimerState> functionTimerStates,
    TimeProvider timeProvider)
{
    public async Task<FunctionTimerState?> InvokeAsync(
        string ownerId,
        int leaseSeconds,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var lease = FunctionExecutionLease.Create(
            ownerId,
            Guid.NewGuid().ToString("N"),
            now,
            now.AddSeconds(leaseSeconds));
        return await functionTimerStates.FindOneAndUpdateAsync(
            FunctionTimerSchedulerFilters.ClaimableAt(now),
            Builders<FunctionTimerState>.Update
                .Set(timer => timer.Lease, lease)
                .Set(timer => timer.UpdatedAtUtc, now),
            new FindOneAndUpdateOptions<FunctionTimerState>
            {
                ReturnDocument = ReturnDocument.After,
                Sort = Builders<FunctionTimerState>.Sort.Ascending(timer => timer.NextOccurrenceUtc)
            },
            cancellationToken);
    }
}
