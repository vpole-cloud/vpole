using MongoDB.Driver;
using Pole.Features.Quotas.Domain;

namespace Pole.Features.Quotas.Application.Persistence.Reservations;

public delegate Task CancelQuotaReservation(
    string reservationId,
    CancellationToken cancellationToken = default);

internal sealed class MongoCancelQuotaReservation(
    IMongoCollection<QuotaReservation> reservations,
    QuotaSettings settings,
    TimeProvider timeProvider)
{
    public async Task InvokeAsync(
        string reservationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reservationId))
            throw new ArgumentException("Quota reservation id is required.", nameof(reservationId));

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var cancelled = await reservations.FindOneAndUpdateAsync(
            QuotaReservationFilters.Active(reservationId),
            Builders<QuotaReservation>.Update.Set(
                reservation => reservation.State,
                QuotaDomainFactory.CancelReservation(nowUtc, settings.FinalizedReservationHistory)),
            new FindOneAndUpdateOptions<QuotaReservation>
            {
                ReturnDocument = ReturnDocument.Before
            },
            cancellationToken);
        if (cancelled is not null)
            return;

        var existing = await reservations
            .Find(QuotaReservationFilters.ById(reservationId))
            .FirstOrDefaultAsync(cancellationToken);
        switch (existing?.State)
        {
            case CancelledQuotaReservationState:
                return;
            case null:
                throw new InvalidOperationException(
                    "Quota reservation disappeared before compensation completed.");
            case CommittedQuotaReservationState:
                throw new InvalidOperationException(
                    "A committed quota reservation cannot be cancelled.");
            case ActiveQuotaReservationState:
                throw new InvalidOperationException(
                    "Quota reservation changed while compensation was in progress.");
            default:
                throw new InvalidOperationException(
                    $"Quota reservation '{reservationId}' has an unsupported state.");
        }
    }
}
