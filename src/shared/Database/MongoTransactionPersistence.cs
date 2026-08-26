using MongoDB.Driver;
using Pole.ResultType;

namespace Pole.Database;

public sealed class MongoRunTransaction(IMongoClient client)
{
    public Task<TOutcome> InvokeOutcomeAsync<TOutcome>(
        Func<
            MongoTransactionContext<TOutcome>,
            CancellationToken,
            Task<MongoTransactionDecision<TOutcome>>> operation,
        CancellationToken cancellationToken = default)
        where TOutcome : notnull =>
        MongoTransactionPersistence.RunOutcomeAsync(
            client,
            operation,
            transactionOptions: null,
            cancellationToken);

}

internal static class MongoTransactionPersistence
{
    public static async Task<TOutcome> RunOutcomeAsync<TOutcome>(
        IMongoClient client,
        Func<
            MongoTransactionContext<TOutcome>,
            CancellationToken,
            Task<MongoTransactionDecision<TOutcome>>> operation,
        TransactionOptions? transactionOptions = null,
        CancellationToken cancellationToken = default)
        where TOutcome : notnull
    {
        using var session = await client.StartSessionAsync(
            cancellationToken: cancellationToken);
        var decision = await session.WithTransactionAsync(
            async (transactionSession, transactionCancellationToken) =>
            {
                var context = new MongoTransactionContext<TOutcome>(transactionSession);
                var transactionDecision = await operation(
                    context,
                    transactionCancellationToken);
                if (transactionDecision is MongoTransactionAbort<TOutcome>)
                {
                    await transactionSession.AbortTransactionAsync(
                        transactionCancellationToken);
                }

                return transactionDecision;
            },
            transactionOptions,
            cancellationToken);

        return decision switch
        {
            MongoTransactionCommit<TOutcome> commit => commit.Outcome,
            MongoTransactionAbort<TOutcome> abort => abort.Outcome
        };
    }

}
