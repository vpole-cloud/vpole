using MongoDB.Driver;

namespace Pole.Database;

public sealed record MongoTransactionCommit<TOutcome>(TOutcome Outcome)
    where TOutcome : notnull;

public sealed record MongoTransactionAbort<TOutcome>(TOutcome Outcome)
    where TOutcome : notnull;

public union MongoTransactionDecision<TOutcome>(
    MongoTransactionCommit<TOutcome>,
    MongoTransactionAbort<TOutcome>)
    where TOutcome : notnull;

/// <summary>
/// Gives a transaction callback access to the active Mongo session and requires it to choose
/// explicitly whether its typed outcome should commit or abort the transaction.
/// </summary>
public sealed class MongoTransactionContext<TOutcome>
    where TOutcome : notnull
{
    internal MongoTransactionContext(IClientSessionHandle session)
    {
        Session = session;
    }

    public IClientSessionHandle Session { get; }

    public MongoTransactionDecision<TOutcome> Commit(TOutcome outcome) =>
        new MongoTransactionCommit<TOutcome>(outcome);

    public MongoTransactionDecision<TOutcome> Abort(TOutcome outcome) =>
        new MongoTransactionAbort<TOutcome>(outcome);
}

public delegate Task<TOutcome> RunMongoOutcomeTransaction<TOutcome>(
    Func<
        MongoTransactionContext<TOutcome>,
        CancellationToken,
        Task<MongoTransactionDecision<TOutcome>>> operation,
    CancellationToken cancellationToken = default)
    where TOutcome : notnull;
