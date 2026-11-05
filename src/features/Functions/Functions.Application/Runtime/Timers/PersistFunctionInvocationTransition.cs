using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Application.Declarations;
using Pole.Features.Functions.Application;
using MongoDB.Driver;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Runtime.Timers;

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
