using MongoDB.Driver;
using Pole.Database;
using Pole.ResultType;

namespace Pole.Tests.Integration.Infrastructure;

public static class MongoTestStoreTransactionExtensions
{
    public static Task<TOutcome> RunOutcomeInTransactionAsync<TOutcome>(
        this MongoTestStore store,
        Func<
            MongoTransactionContext<TOutcome>,
            CancellationToken,
            Task<MongoTransactionDecision<TOutcome>>> callbackAsync,
        CancellationToken cancellationToken = default)
        where TOutcome : notnull =>
        new MongoRunTransaction(store.Client).InvokeOutcomeAsync(
            callbackAsync,
            cancellationToken);

    public static Task<Result> RunInTransactionAsync(
        this MongoTestStore store,
        Func<IClientSessionHandle, CancellationToken, Task<Result>> callbackAsync,
        CancellationToken cancellationToken = default) =>
        new MongoRunTransaction(store.Client).InvokeAsync(
            callbackAsync,
            cancellationToken);

    public static Task<Result<T>> RunInTransactionAsync<T>(
        this MongoTestStore store,
        Func<IClientSessionHandle, CancellationToken, Task<Result<T>>> callbackAsync,
        CancellationToken cancellationToken = default)
        where T : notnull =>
        new MongoRunTransaction(store.Client).InvokeAsync(
            callbackAsync,
            cancellationToken);
}
