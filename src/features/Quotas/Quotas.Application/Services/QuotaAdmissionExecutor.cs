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
            if (!reservationResult.TryGetValue(out reservation, out var reservationFailure))
                return reservationFailure;
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
                    if (commitResult is Failure commitFailure)
                        return commitFailure;
                }

                if (!negativeDelta.IsEmpty)
                {
                    var releaseResult = await admissionService.ApplyUsageDeltaAsync(
                        tenantId,
                        negativeDelta,
                        session,
                        transactionCancellationToken);
                    if (releaseResult is Failure releaseFailure)
                        return releaseFailure;
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
