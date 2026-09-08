using Pole.Database;

namespace Pole.Features.Postgres.Tests.Integration.Infrastructure;

public static class MongoTestStoreTransactionExtensions
{
    public static Task<TOutcome> RunOutcomeInTransactionAsync<TOutcome>(
        this PostgresTestStore store,
        Func<
            MongoTransactionContext<TOutcome>,
            CancellationToken,
            Task<MongoTransactionDecision<TOutcome>>> callbackAsync,
        CancellationToken cancellationToken = default)
        where TOutcome : notnull =>
        new MongoRunTransaction(store.Client).InvokeOutcomeAsync(
            callbackAsync,
            cancellationToken);
}
