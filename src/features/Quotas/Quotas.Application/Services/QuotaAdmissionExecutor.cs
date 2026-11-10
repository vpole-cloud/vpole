using Microsoft.Extensions.Logging;
using Pole.Database;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.Quotas.Application.Reservations;
using Pole.Features.Quotas.Application.Usage;
using Pole.Features.Quotas.Contracts;

namespace Pole.Features.Quotas.Application.Services;

internal sealed class QuotaAdmissionExecutor(
    MongoRunTransaction runMongoTransaction,
    QuotaCatalog catalog,
    ReserveQuotaReservation reserveQuotaReservation,
    CommitQuotaReservation commitQuotaReservation,
    ApplyQuotaUsageDelta applyQuotaUsageDelta,
    IQuotaUsageRelease releaseUsage,
    CancelQuotaReservation cancelQuotaReservation,
    ILogger<QuotaAdmissionExecutor> logger
) : IQuotaAdmissionExecutor
{
    public async Task<TOutcome> ExecuteOutcomeAsync<TSpec, TOutcome>(
        ExecuteQuotaAdmissionRequest<TSpec, TOutcome> request,
        CancellationToken cancellationToken = default)
        where TOutcome : notnull
    {
        ArgumentNullException.ThrowIfNull(request.QuotaError);
        ArgumentNullException.ThrowIfNull(request.MutationCallback);

        var delta = CalculateDelta(catalog, request.Mutation);
        var positiveDelta = QuotaDelta.PositivePart(delta);
        var negativeDelta = QuotaDelta.NegativePart(delta);

        QuotaReservationLease? reservation = null;
        if (!positiveDelta.IsEmpty)
        {
            var reservationResult = await reserveQuotaReservation(
                request.TenantId,
                positiveDelta,
                request.ResourceKey,
                cancellationToken);
            switch (reservationResult)
            {
                case QuotaReservationLease lease:
                    reservation = lease;
                    break;
                case QuotaExceeded error:
                    return request.QuotaError(new QuotaExecutionError(new QuotaReservationError(error)));
                case QuotaReservationConflict error:
                    return request.QuotaError(new QuotaExecutionError(new QuotaReservationError(error)));
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
                        switch (commitResult)
                        {
                            case QuotaReservationCommitted:
                                break;
                            case QuotaReservationNotFound error:
                                return transaction.Abort(
                                    request.QuotaError(
                                        new QuotaExecutionError(new QuotaReservationCommitError(error))));
                            case QuotaReservationCancelled error:
                                return transaction.Abort(
                                    request.QuotaError(
                                        new QuotaExecutionError(new QuotaReservationCommitError(error))));
                            case QuotaReservationExpired error:
                                return transaction.Abort(
                                    request.QuotaError(
                                        new QuotaExecutionError(new QuotaReservationCommitError(error))));
                        }
                    }

                    if (request.Mutation is DeleteQuotaMutation<TSpec> delete)
                        await releaseUsage.ReleaseAsync(
                            new ReleaseQuotaUsageRequest<TSpec>(request.TenantId, delete.Spec),
                            transaction.Session,
                            transactionCancellationToken);
                    else if (!negativeDelta.IsEmpty)
                        await applyQuotaUsageDelta(
                            request.TenantId,
                            negativeDelta,
                            transaction.Session,
                            transactionCancellationToken);

                    var decision = await request.MutationCallback(
                        new QuotaAdmissionTransactionContext<TOutcome>(transaction.Session),
                        transactionCancellationToken);
                    committed = decision is QuotaAdmissionTransactionCommit<TOutcome>;
                    return decision switch
                    {
                        QuotaAdmissionTransactionCommit<TOutcome> commit => transaction.Commit(commit.Outcome),
                        QuotaAdmissionTransactionAbort<TOutcome> abort => transaction.Abort(abort.Outcome),
                    };
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

    private static QuotaDelta CalculateDelta<TSpec>(QuotaCatalog catalog, QuotaMutation<TSpec> mutation) =>
        mutation switch
        {
            CreateQuotaMutation<TSpec> create => catalog.Calculate(create.Spec),
            UpdateQuotaMutation<TSpec> update => QuotaDelta.Difference(
                catalog.Calculate(update.NextSpec),
                catalog.Calculate(update.PreviousSpec)),
            DeleteQuotaMutation<TSpec> delete => QuotaDelta.Difference(
                QuotaDelta.Empty,
                catalog.Calculate(delete.Spec)),
            _ => throw new InvalidOperationException($"Unknown quota mutation type '{mutation.GetType().FullName}'."),
        };

    private async Task CancelBestEffortAsync(QuotaReservationLease? reservation)
    {
        if (reservation is null)
            return;

        try
        {
            await cancelQuotaReservation(reservation.ReservationId, CancellationToken.None);
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
