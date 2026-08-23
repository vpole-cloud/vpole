using Microsoft.Extensions.Logging;
using Pole.Database;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.Quotas.Contracts;
using Pole.Features.Quotas.Application.Persistence;
using Pole.ResultType;

namespace Pole.Features.Quotas.Application.Services;

internal sealed class QuotaAdmissionExecutor(
    MongoRunTransaction runMongoTransaction,
    QuotaCatalog catalog,
    ReserveQuotaReservation reserveQuotaReservation,
    CommitQuotaReservation commitQuotaReservation,
    ApplyQuotaUsageDelta applyQuotaUsageDelta,
    CancelQuotaReservation cancelQuotaReservation,
    ILogger<QuotaAdmissionExecutor> logger) : IQuotaAdmissionExecutor
{
    public async Task<TOutcome> ExecuteOutcomeAsync<TSpec, TOutcome>(
        string tenantId,
        QuotaResourceKey resourceKey,
        QuotaMutation<TSpec> mutation,
        Func<QuotaExecutionError, TOutcome> quotaError,
        Func<
            MongoTransactionContext<TOutcome>,
            CancellationToken,
            Task<MongoTransactionDecision<TOutcome>>> mutationCallback,
        CancellationToken cancellationToken = default)
        where TOutcome : notnull
    {
        ArgumentNullException.ThrowIfNull(quotaError);
        ArgumentNullException.ThrowIfNull(mutationCallback);

        var delta = CalculateDelta(catalog, mutation);
        var positiveDelta = QuotaDelta.PositivePart(delta);
        var negativeDelta = QuotaDelta.NegativePart(delta);

        QuotaReservationLease? reservation = null;
        if (!positiveDelta.IsEmpty)
        {
            var reservationResult = await reserveQuotaReservation(
                tenantId,
                positiveDelta,
                resourceKey,
                cancellationToken);
            if (!reservationResult.TryGetValue(out reservation, out var reservationError))
            {
                return quotaError(new QuotaExecutionError(reservationError));
            }
        }

        var committed = false;
        try
        {
            var outcome = await runMongoTransaction.InvokeOutcomeAsync<TOutcome>(
                async (transaction, transactionCancellationToken) =>
                {
                    committed = false;
                    if (reservation is not null)
                    {
                        var commitResult = await commitQuotaReservation(
                            reservation.ReservationId,
                            transaction.Session,
                            transactionCancellationToken);
                        if (!commitResult.TryGetValue(out _, out var commitError))
                        {
                            return transaction.Abort(
                                quotaError(new QuotaExecutionError(commitError)));
                        }
                    }

                    if (!negativeDelta.IsEmpty)
                    {
                        await applyQuotaUsageDelta(
                            tenantId,
                            negativeDelta,
                            transaction.Session,
                            transactionCancellationToken);
                    }

                    var decision = await mutationCallback(
                        transaction,
                        transactionCancellationToken);
                    committed = decision is MongoTransactionCommit<TOutcome>;
                    return decision;
                },
                cancellationToken: cancellationToken);

            if (!committed)
                await CancelBestEffortAsync(reservation);

            return outcome;
        }
        catch
        {
            await CancelBestEffortAsync(reservation);
            throw;
        }
    }

    private static QuotaDelta CalculateDelta<TSpec>(
        QuotaCatalog catalog,
        QuotaMutation<TSpec> mutation) =>
        mutation switch
        {
            CreateQuotaMutation<TSpec> create => catalog.Calculate(create.Spec),
            UpdateQuotaMutation<TSpec> update => QuotaDelta.Difference(
                catalog.Calculate(update.NextSpec),
                catalog.Calculate(update.PreviousSpec)),
            DeleteQuotaMutation<TSpec> delete => QuotaDelta.Difference(
                QuotaDelta.Empty,
                catalog.Calculate(delete.Spec)),
            _ => throw new InvalidOperationException(
                $"Unknown quota mutation type '{mutation.GetType().FullName}'.")
        };

    private async Task CancelBestEffortAsync(QuotaReservationLease? reservation)
    {
        if (reservation is null)
            return;

        try
        {
            await cancelQuotaReservation(
                reservation.ReservationId,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to cancel quota reservation {ReservationId} during compensation",
                reservation.ReservationId);
        }
    }
}
