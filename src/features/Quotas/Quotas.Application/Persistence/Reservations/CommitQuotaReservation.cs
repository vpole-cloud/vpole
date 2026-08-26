using MongoDB.Driver;
using Pole.Features.Quotas.Application.Persistence.Usage;
using Pole.Features.Quotas.Contracts;
using Pole.Features.Quotas.Domain;
using Pole.ResultType;

namespace Pole.Features.Quotas.Application.Persistence.Reservations;

public delegate Task<Result<QuotaReservationCommitted, QuotaReservationCommitError>>
    CommitQuotaReservation(
        string reservationId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

internal sealed class MongoCommitQuotaReservation(
    IMongoCollection<QuotaReservation> reservations,
    IMongoCollection<TenantQuotaUsage> usages,
    QuotaSettings settings,
    TimeProvider timeProvider)
{
    public async Task<Result<QuotaReservationCommitted, QuotaReservationCommitError>> InvokeAsync(
        string reservationId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reservationId))
            throw new ArgumentException("Quota reservation id is required.", nameof(reservationId));
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Quota reservation commit requires an active transaction.");

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var finalized = await reservations.FindOneAndUpdateAsync(
            session,
            QuotaReservationFilters.Active(reservationId, nowUtc),
            Builders<QuotaReservation>.Update.Set(
                reservation => reservation.State,
                QuotaDomainFactory.CommitReservation(nowUtc, settings.FinalizedReservationHistory)),
            new FindOneAndUpdateOptions<QuotaReservation, QuotaReservation>
            {
                ReturnDocument = ReturnDocument.Before
            },
            cancellationToken);

        if (finalized is null)
        {
            var existing = await reservations
                .Find(session, QuotaReservationFilters.ById(reservationId))
                .FirstOrDefaultAsync(cancellationToken);
            return existing?.State switch
            {
                null => new Err<QuotaReservationCommitError>(
                    new QuotaReservationCommitError(new QuotaReservationNotFound(reservationId))),
                CommittedQuotaReservationState => new Ok<QuotaReservationCommitted>(
                    new QuotaReservationCommitted()),
                CancelledQuotaReservationState => new Err<QuotaReservationCommitError>(
                    new QuotaReservationCommitError(new QuotaReservationCancelled(reservationId))),
                ActiveQuotaReservationState => new Err<QuotaReservationCommitError>(
                    new QuotaReservationCommitError(new QuotaReservationExpired(reservationId))),
                _ => throw new InvalidOperationException(
                    $"Quota reservation '{reservationId}' has an unsupported state.")
            };
        }

        var usage = await MongoQuotaUsage.TouchAsync(
            usages,
            session,
            finalized.TenantId,
            nowUtc,
            cancellationToken);
        var values = QuotaValues.Add(usage.Values, finalized.Delta);
        await usages.UpdateOneAsync(
            session,
            QuotaUsageFilters.ByTenant(finalized.TenantId),
            Builders<TenantQuotaUsage>.Update
                .Set(candidate => candidate.Values, values)
                .Set(candidate => candidate.UpdatedAtUtc, nowUtc),
            cancellationToken: cancellationToken);

        return new Ok<QuotaReservationCommitted>(new QuotaReservationCommitted());
    }
}
