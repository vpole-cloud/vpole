using MongoDB.Driver;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Persistence.Runtime.Timers;

public delegate Task<bool> PersistFunctionInvocationTransition(
    FunctionInvocation current,
    FunctionInvocation next,
    FunctionMeterEvent? meter,
    CancellationToken cancellationToken = default);

public sealed class MongoPersistFunctionInvocationTransition(
    IMongoClient mongoClient,
    IMongoCollection<FunctionInvocation> functionInvocations,
    IMongoCollection<FunctionMeterEvent> functionMeterEvents)
{
    public async Task<bool> InvokeAsync(
        FunctionInvocation current,
        FunctionInvocation next,
        FunctionMeterEvent? meter,
        CancellationToken cancellationToken = default)
    {
        using var session = await mongoClient.StartSessionAsync(cancellationToken: cancellationToken);
        return await session.WithTransactionAsync(async (transaction, transactionCancellationToken) =>
        {
            var replaced = await functionInvocations.ReplaceOneAsync(
                transaction,
                FunctionInvocationTransitionFilters.CurrentLeasedAttempt(current),
                next,
                cancellationToken: transactionCancellationToken);
            if (replaced.ModifiedCount != 1)
                return false;
            if (meter is not null)
            {
                await functionMeterEvents.InsertOneAsync(
                    transaction,
                    meter,
                    cancellationToken: transactionCancellationToken);
            }
            return true;
        }, cancellationToken: cancellationToken);
    }
}

internal static class FunctionInvocationTransitionFilters
{
    public static FilterDefinition<FunctionInvocation> CurrentLeasedAttempt(FunctionInvocation invocation) =>
        Builders<FunctionInvocation>.Filter.And(
            Builders<FunctionInvocation>.Filter.Eq(candidate => candidate.Id, invocation.Id),
            Builders<FunctionInvocation>.Filter.Eq(candidate => candidate.State, invocation.State),
            Builders<FunctionInvocation>.Filter.Eq(candidate => candidate.AttemptNumber, invocation.AttemptNumber),
            Builders<FunctionInvocation>.Filter.Ne(candidate => candidate.Lease, null),
            Builders<FunctionInvocation>.Filter.Eq(item => item.Lease!.Token, invocation.Lease!.Token));
}
