using Pole.Database;

namespace Pole.Features.RedisServices.Tests.Integration.Infrastructure;

public static class MongoTestStoreTransactionExtensions
{
    public static Task<TOutcome> RunOutcomeInTransactionAsync<TOutcome>(
        this RedisTestStore store,
        Func<MongoTransactionContext<TOutcome>, CancellationToken, Task<MongoTransactionDecision<TOutcome>>>
            callbackAsync,
        CancellationToken cancellationToken = default)
        where TOutcome : notnull =>
        new MongoRunTransaction(store.Client).InvokeOutcomeAsync(callbackAsync, cancellationToken);
}
