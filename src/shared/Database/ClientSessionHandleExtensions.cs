using MongoDB.Driver;
using Pole.ResultType;

namespace Pole.Database;

public static class ClientSessionHandleExtensions
{
    public static Task<TOutcome> RunOutcomeInTransactionAsync<TOutcome>(
        this IMongoDbContext dbContext,
        Func<
            MongoTransactionContext<TOutcome>,
            CancellationToken,
            Task<MongoTransactionDecision<TOutcome>>> callbackAsync,
        TransactionOptions? transactionOptions = null,
        CancellationToken cancellationToken = default)
        where TOutcome : notnull =>
        MongoTransactionPersistence.RunOutcomeAsync(
            dbContext.Client,
            callbackAsync,
            transactionOptions,
            cancellationToken);

    public static Task<Result> RunInTransactionAsync(
        this IMongoDbContext dbContext,
        Func<IClientSessionHandle, CancellationToken, Task<Result>> callbackAsync,
        TransactionOptions? transactionOptions = null,
        CancellationToken cancellationToken = default) =>
        MongoTransactionPersistence.RunAsync(
            dbContext.Client,
            callbackAsync,
            transactionOptions,
            cancellationToken);

    public static Task<Result<T>> RunInTransactionAsync<T>(
        this IMongoDbContext dbContext,
        Func<IClientSessionHandle, CancellationToken, Task<Result<T>>> callbackAsync,
        TransactionOptions? transactionOptions = null,
        CancellationToken cancellationToken = default)
        where T : notnull =>
        MongoTransactionPersistence.RunAsync(
            dbContext.Client,
            callbackAsync,
            transactionOptions,
            cancellationToken);
}
