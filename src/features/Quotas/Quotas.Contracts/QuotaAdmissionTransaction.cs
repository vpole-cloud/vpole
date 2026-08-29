using MongoDB.Driver;

namespace Pole.Features.Quotas.Contracts;

public sealed record QuotaAdmissionTransactionCommit<TOutcome>(TOutcome Outcome)
    where TOutcome : notnull;

public sealed record QuotaAdmissionTransactionAbort<TOutcome>(TOutcome Outcome)
    where TOutcome : notnull;

public union QuotaAdmissionTransactionDecision<TOutcome>(
    QuotaAdmissionTransactionCommit<TOutcome>,
    QuotaAdmissionTransactionAbort<TOutcome>)
    where TOutcome : notnull;

/// <summary>
/// Gives a quota-guarded mutation access to the caller-owned Mongo session and requires an
/// explicit decision to commit or abort its typed outcome.
/// </summary>
public sealed class QuotaAdmissionTransactionContext<TOutcome>
    where TOutcome : notnull
{
    public QuotaAdmissionTransactionContext(IClientSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
    }

    public IClientSessionHandle Session { get; }

    public QuotaAdmissionTransactionDecision<TOutcome> Commit(TOutcome outcome) =>
        new QuotaAdmissionTransactionCommit<TOutcome>(outcome);

    public QuotaAdmissionTransactionDecision<TOutcome> Abort(TOutcome outcome) =>
        new QuotaAdmissionTransactionAbort<TOutcome>(outcome);
}
