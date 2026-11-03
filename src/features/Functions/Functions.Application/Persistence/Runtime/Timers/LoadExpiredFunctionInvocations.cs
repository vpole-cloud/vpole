using MongoDB.Driver;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Persistence.Runtime.Timers;

public delegate Task<IReadOnlyList<FunctionInvocation>> LoadExpiredFunctionInvocations(
    DateTimeOffset nowUtc,
    int take,
    CancellationToken cancellationToken = default);

public sealed class MongoLoadExpiredFunctionInvocations(
    IMongoCollection<FunctionInvocation> functionInvocations)
{
    public async Task<IReadOnlyList<FunctionInvocation>> InvokeAsync(
        DateTimeOffset nowUtc,
        int take,
        CancellationToken cancellationToken = default) =>
        await functionInvocations.Find(FunctionInvocationDispatchFilters.ExpiredAttemptAt(nowUtc))
            .Limit(take)
            .ToListAsync(cancellationToken);
}

internal static class FunctionInvocationDispatchFilters
{
    public static FilterDefinition<FunctionInvocation> ExpiredAttemptAt(DateTimeOffset nowUtc) =>
        Builders<FunctionInvocation>.Filter.And(
            Builders<FunctionInvocation>.Filter.In(
                invocation => invocation.State,
                [FunctionInvocationState.Dispatching, FunctionInvocationState.Running]),
            Builders<FunctionInvocation>.Filter.Ne(invocation => invocation.Lease, null),
            Builders<FunctionInvocation>.Filter.Lte(item => item.Lease!.ExpiresAtUtc, nowUtc));
}
