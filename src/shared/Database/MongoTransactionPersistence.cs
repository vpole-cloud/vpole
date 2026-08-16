using MongoDB.Driver;
using Pole.ResultType;

namespace Pole.Database;

public delegate Task<Result> RunMongoTransaction(
    Func<IClientSessionHandle, CancellationToken, Task<Result>> operation,
    CancellationToken cancellationToken = default);

public delegate Task<Result<T>> RunMongoTransaction<T>(
    Func<IClientSessionHandle, CancellationToken, Task<Result<T>>> operation,
    CancellationToken cancellationToken = default)
    where T : notnull;

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

    public Task<Result> InvokeAsync(
        Func<IClientSessionHandle, CancellationToken, Task<Result>> operation,
        CancellationToken cancellationToken = default) =>
        MongoTransactionPersistence.RunAsync(
            client,
            operation,
            transactionOptions: null,
            cancellationToken);

    public Task<Result<T>> InvokeAsync<T>(
        Func<IClientSessionHandle, CancellationToken, Task<Result<T>>> operation,
        CancellationToken cancellationToken = default)
        where T : notnull =>
        MongoTransactionPersistence.RunAsync(
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

    public static async Task<Result> RunAsync(
        IMongoClient client,
        Func<IClientSessionHandle, CancellationToken, Task<Result>> operation,
        TransactionOptions? transactionOptions = null,
        CancellationToken cancellationToken = default)
    {
        using var session = await client.StartSessionAsync(
            cancellationToken: cancellationToken);
        try
        {
            return await session.WithTransactionAsync(
                async (transactionSession, transactionCancellationToken) =>
                {
                    var result = await operation(
                        transactionSession,
                        transactionCancellationToken);
                    if (result is not ISuccess)
                        throw new TransactionAbortedByResultException(result);

                    return result;
                },
                transactionOptions,
                cancellationToken);
        }
        catch (TransactionAbortedByResultException exception)
        {
            return exception.Result;
        }
        catch (MongoWriteException exception) when (
            exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Failures.Conflict(
                "A conflict occurred due to duplicate key error.");
        }
        catch (MongoBulkWriteException exception) when (
            exception.WriteErrors.Any(error =>
                error.Category == ServerErrorCategory.DuplicateKey))
        {
            return Failures.Conflict(
                "A conflict occurred due to duplicate key error.");
        }
    }

    public static async Task<Result<T>> RunAsync<T>(
        IMongoClient client,
        Func<IClientSessionHandle, CancellationToken, Task<Result<T>>> operation,
        TransactionOptions? transactionOptions = null,
        CancellationToken cancellationToken = default)
        where T : notnull
    {
        using var session = await client.StartSessionAsync(
            cancellationToken: cancellationToken);
        try
        {
            return await session.WithTransactionAsync(
                async (transactionSession, transactionCancellationToken) =>
                {
                    var result = await operation(
                        transactionSession,
                        transactionCancellationToken);
                    if (result is not ISuccess)
                        throw new TransactionAbortedByResultException(result);

                    return result;
                },
                transactionOptions,
                cancellationToken);
        }
        catch (TransactionAbortedByResultException exception) when (
            exception.Result is Result<T> result)
        {
            return result;
        }
        catch (MongoWriteException exception) when (
            exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Failures.Conflict(
                "A conflict occurred due to duplicate key error.");
        }
        catch (MongoBulkWriteException exception) when (
            exception.WriteErrors.Any(error =>
                error.Category == ServerErrorCategory.DuplicateKey))
        {
            return Failures.Conflict(
                "A conflict occurred due to duplicate key error.");
        }
    }

    private sealed class TransactionAbortedByResultException(Result result)
        : Exception
    {
        public Result Result { get; } = result;
    }
}
