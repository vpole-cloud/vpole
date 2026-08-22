using MongoDB.Driver;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Persistence.Runtime.Timers;

public delegate Task<FunctionInvocation?> ClaimNextFunctionInvocation(
    string ownerId,
    int leaseSeconds,
    CancellationToken cancellationToken = default);

public sealed class MongoClaimNextFunctionInvocation(
    IMongoCollection<FunctionInvocation> functionInvocations,
    TimeProvider timeProvider)
{
    public async Task<FunctionInvocation?> InvokeAsync(
        string ownerId,
        int leaseSeconds,
        CancellationToken cancellationToken = default)
    {
        for (var retry = 0; retry < 5; retry++)
        {
            var now = timeProvider.GetUtcNow();
            var candidate = await functionInvocations.Find(
                    FunctionInvocationClaimFilters.ReadyAt(now))
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

internal static class FunctionInvocationClaimFilters
{
    public static FilterDefinition<FunctionInvocation> ReadyAt(DateTimeOffset nowUtc) =>
        Builders<FunctionInvocation>.Filter.Or(
            Builders<FunctionInvocation>.Filter.And(
                Builders<FunctionInvocation>.Filter.Eq(
                    invocation => invocation.State,
                    FunctionInvocationState.Pending),
                Builders<FunctionInvocation>.Filter.Or(
                    Builders<FunctionInvocation>.Filter.Eq(invocation => invocation.NextAttemptAtUtc, null),
                    Builders<FunctionInvocation>.Filter.Lte(invocation => invocation.NextAttemptAtUtc, nowUtc))),
            Builders<FunctionInvocation>.Filter.And(
                Builders<FunctionInvocation>.Filter.Eq(
                    invocation => invocation.State,
                    FunctionInvocationState.RetryScheduled),
                Builders<FunctionInvocation>.Filter.Lte(invocation => invocation.NextAttemptAtUtc, nowUtc)));

    public static FilterDefinition<FunctionInvocation> CurrentCandidate(FunctionInvocation invocation) =>
        Builders<FunctionInvocation>.Filter.And(
            Builders<FunctionInvocation>.Filter.Eq(candidate => candidate.Id, invocation.Id),
            Builders<FunctionInvocation>.Filter.Eq(candidate => candidate.State, invocation.State),
            Builders<FunctionInvocation>.Filter.Eq(candidate => candidate.AttemptNumber, invocation.AttemptNumber),
            Builders<FunctionInvocation>.Filter.Eq(candidate => candidate.UpdatedAtUtc, invocation.UpdatedAtUtc),
            Builders<FunctionInvocation>.Filter.Eq(candidate => candidate.Lease, null));
}
