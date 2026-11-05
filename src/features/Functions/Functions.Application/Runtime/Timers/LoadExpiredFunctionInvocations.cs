using MongoDB.Driver;
using Pole.Features.Functions.Application;
using Pole.Features.Functions.Application.Declarations;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Runtime.Timers;

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
