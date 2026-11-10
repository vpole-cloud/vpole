using MongoDB.Driver;

namespace Pole.Database;

public sealed class MongoRunTransaction(
    IMongoClient client
)
{
    public Task<TOutcome> InvokeOutcomeAsync<TOutcome>(
        Func<MongoTransactionContext<TOutcome>, CancellationToken, Task<MongoTransactionDecision<TOutcome>>> operation,
        CancellationToken cancellationToken = default)
        where TOutcome : notnull => MongoTransactionPersistence.RunOutcomeAsync(
        client,
        operation,
        transactionOptions: null,
        cancellationToken);
}

internal static class MongoTransactionPersistence
{
    public static async Task<TOutcome> RunOutcomeAsync<TOutcome>(
        IMongoClient client,
        Func<MongoTransactionContext<TOutcome>, CancellationToken, Task<MongoTransactionDecision<TOutcome>>> operation,
        TransactionOptions? transactionOptions = null,
        CancellationToken cancellationToken = default)
        where TOutcome : notnull
    {
        using var session = await client.StartSessionAsync(cancellationToken: cancellationToken);
        var decision = await session.WithTransactionAsync(
            async (transactionSession, transactionCancellationToken) =>
            {
                try
                {
                    var context = new MongoTransactionContext<TOutcome>(transactionSession);
                    var transactionDecision = await operation(context, transactionCancellationToken);
                    if (transactionDecision is MongoTransactionAbort<TOutcome>)
                        await AbortAsync(transactionSession);

                    return transactionDecision;
                }
                catch (Exception original)
                {
                    // Abort before the driver marks the session aborted using the cancelled request token.
                    if (transactionSession.IsInTransaction)
                        try
                        {
                            await AbortAsync(transactionSession);
                        }
                        catch (Exception cleanupFailure)
                        {
                            original.Data["Pole.MongoTransaction.AbortFailure"] = cleanupFailure;
                        }

                    throw;
                }
            },
            transactionOptions,
            cancellationToken);

        return decision switch
        {
            MongoTransactionCommit<TOutcome> commit => commit.Outcome,
            MongoTransactionAbort<TOutcome> abort => abort.Outcome,
        };
    }

    private static async Task AbortAsync(IClientSessionHandle session)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await session.AbortTransactionAsync(cleanup.Token);
    }
}
