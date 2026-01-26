using MongoDB.Driver;
using Pole.Server.Features.Common.Exeptions;
using Pole.Server.ResultType;

namespace Pole.Server.Database;

public static class ClientSessionHandleExtensions
{
    extension(MongoDbContext dbContext)
    {
        public async Task<Result> RunInTransactionAsync(
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
            catch (ConflictExeption e)
            {
                return Result.Conflict(e.Message);
            }
        }
        public async Task<Result<T>> RunInTransactionAsync<T>(
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
            catch (ConflictExeption e)
            {
                return Result.Conflict(e.Message);
            }
        }
    }
    
}