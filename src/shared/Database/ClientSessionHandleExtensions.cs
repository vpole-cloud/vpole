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
                callbackAsync,
                transactionOptions,
                cancellationToken);
        }
        catch (MongoWriteException e) when (e.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Result.Conflict("A conflict occurred due to duplicate key error.");
        }
        catch (MongoBulkWriteException e) when (e.WriteErrors.Any(x => x.Category == ServerErrorCategory.DuplicateKey))
        {
            return Result.Conflict("A conflict occurred due to duplicate key error.");
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
                callbackAsync,
                transactionOptions,
                cancellationToken);
        }
        catch (MongoWriteException e) when (e.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Result.Conflict("A conflict occurred due to duplicate key error.");
        }
        catch (MongoBulkWriteException e) when (e.WriteErrors.Any(x => x.Category == ServerErrorCategory.DuplicateKey))
        {
            return Result.Conflict("A conflict occurred due to duplicate key error.");
        }
    }
    
}