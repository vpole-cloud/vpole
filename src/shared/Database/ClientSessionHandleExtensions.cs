using MongoDB.Driver;
using Pole.ResultType;

namespace Pole.Database;

public static class ClientSessionHandleExtensions
{
    public static async Task<Result> RunInTransactionAsync(
        this IMongoDbContext dbContext,
        Func<IClientSessionHandle, CancellationToken, Task<Result>> callbackAsync,
        TransactionOptions? transactionOptions = null,
        CancellationToken cancellationToken = default)
    {
        using var session = await dbContext.Client.StartSessionAsync(cancellationToken: cancellationToken);
        try
        {
            return await session.WithTransactionAsync(
                async (transactionSession, transactionCancellationToken) =>
                {
                    var result = await callbackAsync(transactionSession, transactionCancellationToken);
                    if (result is not ISuccess)
                        throw new TransactionAbortedByResultException(result);

                    return result;
                },
                transactionOptions,
                cancellationToken);
        }
        catch (TransactionAbortedByResultException e)
        {
            return e.Result;
        }
        catch (MongoWriteException e) when (e.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Failures.Conflict("A conflict occurred due to duplicate key error.");
        }
        catch (MongoBulkWriteException e) when (e.WriteErrors.Any(x => x.Category == ServerErrorCategory.DuplicateKey))
        {
            return Failures.Conflict("A conflict occurred due to duplicate key error.");
        }
    }
    public static async Task<Result<T>> RunInTransactionAsync<T>(
        this IMongoDbContext dbContext,
        Func<IClientSessionHandle, CancellationToken, Task<Result<T>>> callbackAsync,
        TransactionOptions? transactionOptions = null,
        CancellationToken cancellationToken = default) where T : notnull
    {
        using var session = await dbContext.Client.StartSessionAsync(cancellationToken: cancellationToken);
        try
        {
            return await session.WithTransactionAsync(
                async (transactionSession, transactionCancellationToken) =>
                {
                    var result = await callbackAsync(transactionSession, transactionCancellationToken);
                    if (result is not ISuccess)
                        throw new TransactionAbortedByResultException(result);

                    return result;
                },
                transactionOptions,
                cancellationToken);
        }
        catch (TransactionAbortedByResultException e) when (e.Result is Result<T> result)
        {
            return result;
        }
        catch (MongoWriteException e) when (e.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Failures.Conflict("A conflict occurred due to duplicate key error.");
        }
        catch (MongoBulkWriteException e) when (e.WriteErrors.Any(x => x.Category == ServerErrorCategory.DuplicateKey))
        {
            return Failures.Conflict("A conflict occurred due to duplicate key error.");
        }
    }

    private sealed class TransactionAbortedByResultException(Result result) : Exception
    {
        public Result Result { get; } = result;
    }
}
