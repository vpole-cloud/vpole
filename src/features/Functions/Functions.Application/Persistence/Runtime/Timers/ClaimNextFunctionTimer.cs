using MongoDB.Driver;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Persistence.Runtime.Timers;

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

internal static class FunctionTimerSchedulerFilters
{
    public static FilterDefinition<FunctionTimerState> ClaimableAt(DateTimeOffset nowUtc) =>
        Builders<FunctionTimerState>.Filter.And(
            Builders<FunctionTimerState>.Filter.Eq(timer => timer.Enabled, true),
            Builders<FunctionTimerState>.Filter.Lte(timer => timer.NextOccurrenceUtc, nowUtc),
            Builders<FunctionTimerState>.Filter.Or(
                Builders<FunctionTimerState>.Filter.Eq(timer => timer.Lease, null),
                Builders<FunctionTimerState>.Filter.Lte(item => item.Lease!.ExpiresAtUtc, nowUtc)));
}
