using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.Quotas.Contracts;
using Pole.ResultType;

namespace Pole.Features.Quotas.Application.Services;

public sealed class QuotaAdmissionExecutor(
    IQuotaDbContext dbContext,
    QuotaCatalog catalog,
    IQuotaAdmissionService admissionService,
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
            var reservationResult = await admissionService.ReserveAsync(
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
            var outcome = await dbContext.RunOutcomeInTransactionAsync<TOutcome>(
                async (transaction, transactionCancellationToken) =>
                {
                    committed = false;
                    if (reservation is not null)
                    {
                        var commitResult = await admissionService.CommitAsync(
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
                        var releaseResult = await admissionService.ApplyUsageDeltaAsync(
                            tenantId,
                            negativeDelta,
                            transaction.Session,
                            transactionCancellationToken);
                        if (!releaseResult.TryGetValue(out _, out var releaseError))
                        {
                            return transaction.Abort(
                                quotaError(new QuotaExecutionError(releaseError)));
                        }
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

    public Task<Result<TResponse>> ExecuteAsync<TSpec, TResponse>(
        string tenantId,
        QuotaResourceKey resourceKey,
        QuotaMutation<TSpec> mutation,
        Func<IClientSessionHandle, CancellationToken, Task<Result<TResponse>>> mutationCallback,
        CancellationToken cancellationToken = default)
        where TResponse : notnull =>
        ExecuteAsync<TSpec, NoPreparation, TResponse>(
            tenantId,
            resourceKey,
            mutation,
            _ => Task.FromResult<Result<NoPreparation>>(NoPreparation.Instance),
            (_, session, transactionCancellationToken) =>
                mutationCallback(session, transactionCancellationToken),
            cancellationToken);

    public async Task<Result<TResponse>> ExecuteAsync<TSpec, TPreparation, TResponse>(
        string tenantId,
        QuotaResourceKey resourceKey,
        QuotaMutation<TSpec> mutation,
        Func<CancellationToken, Task<Result<TPreparation>>> preparationCallback,
        Func<TPreparation, IClientSessionHandle, CancellationToken, Task<Result<TResponse>>> mutationCallback,
        CancellationToken cancellationToken = default)
        where TPreparation : notnull
        where TResponse : notnull
    {
        var delta = CalculateDelta(catalog, mutation);
        var positiveDelta = QuotaDelta.PositivePart(delta);
        var negativeDelta = QuotaDelta.NegativePart(delta);

        QuotaReservationLease? reservation = null;
        if (!positiveDelta.IsEmpty)
        {
            var reservationResult = await admissionService.ReserveAsync(
                tenantId,
                positiveDelta,
                resourceKey,
                cancellationToken);
            if (!reservationResult.TryGetValue(out reservation, out var reservationError))
                return ToLegacyFailure(reservationError);
        }

        TPreparation preparation;
        try
        {
            var preparationResult = await preparationCallback(cancellationToken);
            if (preparationResult is not Success<TPreparation> preparationSuccess)
            {
                await CancelBestEffortAsync(reservation);
                return ((Failure<TPreparation>)preparationResult).Error;
            }

            preparation = preparationSuccess.Value;
        }
        catch
        {
            await CancelBestEffortAsync(reservation);
            throw;
        }

        try
        {
            var result = await dbContext.RunInTransactionAsync<TResponse>(async (session, transactionCancellationToken) =>
            {
                if (reservation is not null)
                {
                    var commitResult = await admissionService.CommitAsync(
                        reservation.ReservationId,
                        session,
                        transactionCancellationToken);
                    if (!commitResult.TryGetValue(out _, out var commitError))
                        return ToLegacyFailure(commitError);
                }

                if (!negativeDelta.IsEmpty)
                {
                    var releaseResult = await admissionService.ApplyUsageDeltaAsync(
                        tenantId,
                        negativeDelta,
                        session,
                        transactionCancellationToken);
                    if (!releaseResult.TryGetValue(out _, out var releaseError))
                        return ToLegacyFailure(releaseError);
                }

                return await mutationCallback(preparation, session, transactionCancellationToken);
            }, cancellationToken: cancellationToken);

            if (result is not ISuccess)
                await CancelBestEffortAsync(reservation);

            return result;
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

    private static Failure ToLegacyFailure(
        QuotaReservationError error) => error switch
        {
            InvalidQuotaReservationRequest invalid =>
                Failures.ValidationFailure(
                    invalid.Errors.ToDictionary(
                        item => item.Key,
                        item => item.Value)),
            QuotaExceeded exceeded => Failures.Conflict(
                $"Quota exceeded for {exceeded.Quota.Value}: requested total " +
                $"{checked(exceeded.Current + exceeded.Requested)}, limit " +
                $"{exceeded.Limit}."),
            QuotaReservationConflict => Failures.Conflict(
                "Quota admission conflicted with another request. Please retry.")
        };

    private static Failure ToLegacyFailure(
        QuotaReservationCommitError error) => error switch
        {
            InvalidQuotaReservationCommit invalid =>
                Failures.ValidationFailure(
                    invalid.Errors.ToDictionary(item => item.Key, item => item.Value)),
            QuotaReservationNotFound notFound => Failures.NotFound(
                "Quota reservation not found.",
                notFound.ReservationId),
            QuotaReservationCancelled => Failures.Conflict(
                "Quota reservation has already been cancelled."),
            QuotaReservationExpired => Failures.Conflict(
                "Quota reservation has expired and cannot be committed.")
        };

    private static Failure ToLegacyFailure(
        InvalidQuotaUsageAdjustment error) =>
        Failures.ValidationFailure(
            error.Errors.ToDictionary(item => item.Key, item => item.Value));

    private async Task CancelBestEffortAsync(QuotaReservationLease? reservation)
    {
        if (reservation is null)
            return;

        try
        {
            var cancellationResult = await admissionService.CancelAsync(
                reservation.ReservationId,
                CancellationToken.None);
            if (cancellationResult is Failure cancellationFailure)
            {
                logger.LogWarning(
                    "Failed to cancel quota reservation {ReservationId}: {Failure}",
                    reservation.ReservationId,
                    cancellationFailure.Message);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to cancel quota reservation {ReservationId} during compensation",
                reservation.ReservationId);
        }
    }

    private sealed record NoPreparation
    {
        public static NoPreparation Instance { get; } = new();
    }
}
