using MongoDB.Driver;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Runtime.Timers;

public delegate Task<FunctionInvocation?> ClaimNextFunctionInvocation(
    string ownerId,
    int leaseSeconds,
    CancellationToken cancellationToken = default);

public sealed class MongoClaimNextFunctionInvocation(
    IMongoCollection<FunctionInvocation> functionInvocations,
    TimeProvider timeProvider
)
{
    public async Task<FunctionInvocation?> InvokeAsync(
        string ownerId,
        int leaseSeconds,
        CancellationToken cancellationToken = default)
    {
        for (var retry = 0; retry < 5; retry++)
        {
            var now = timeProvider.GetUtcNow();
            var candidate = await functionInvocations.Find(FunctionInvocationClaimFilters.ReadyAt(now))
                .SortBy(invocation => invocation.NextAttemptAtUtc)
                .ThenBy(invocation => invocation.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);
            if (candidate is null)
                return null;

            var leased = candidate.AcquireDispatchLease(
                ownerId,
                Guid.NewGuid().ToString("N"),
                now,
                now.AddSeconds(leaseSeconds));
            var replaced = await functionInvocations.ReplaceOneAsync(
                FunctionInvocationClaimFilters.CurrentCandidate(candidate),
                leased,
                cancellationToken: cancellationToken);
            if (replaced.ModifiedCount == 1)
                return leased;
        }

        return null;
    }
}
