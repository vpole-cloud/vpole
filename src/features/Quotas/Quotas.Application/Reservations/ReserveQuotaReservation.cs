using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Quotas.Application.Mongo;
using Pole.Features.Quotas.Application.Usage;
using Pole.Features.Quotas.Contracts;
using Pole.Features.Quotas.Domain;

namespace Pole.Features.Quotas.Application.Reservations;

public union ReserveQuotaReservationOutcome(
    QuotaReservationLease,
    QuotaExceeded,
    QuotaReservationConflict);

public delegate Task<ReserveQuotaReservationOutcome> ReserveQuotaReservation(
    string tenantId,
    QuotaDelta delta,
    QuotaResourceKey resourceKey,
    CancellationToken cancellationToken = default);

internal sealed class MongoReserveQuotaReservation(
    IMongoCollection<TenantQuotaPolicy> policies,
    IMongoCollection<TenantQuotaUsage> usages,
    IMongoCollection<QuotaReservation> reservations,
    MongoRunTransaction runMongoTransaction,
    QuotaSettings settings,
    TimeProvider timeProvider
)
{
    private const int InitialUpsertRetryCount = 3;

    public async Task<ReserveQuotaReservationOutcome> InvokeAsync(
        string tenantId,
        QuotaDelta delta,
        QuotaResourceKey resourceKey,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(tenantId, delta, resourceKey);

        for (var attempt = 0; attempt < InitialUpsertRetryCount; attempt++)
            try
            {
                return await runMongoTransaction.InvokeOutcomeAsync<ReserveQuotaReservationOutcome>(
                    async (transaction, ct) =>
                    {
                        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
                        var usage = await MongoQuotaUsage.TouchAsync(usages, transaction.Session, tenantId, nowUtc, ct);
                        var policy = await policies.Find(transaction.Session, QuotaPolicyFilters.ByTenant(tenantId))
                            .FirstOrDefaultAsync(ct);
                        var activeReservations = await reservations.Find(
                                transaction.Session,
                                QuotaReservationFilters.ActiveForTenant(tenantId, nowUtc))
                            .ToListAsync(ct);

                        var denial = GetAdmissionDenial(policy, usage, activeReservations, delta);
                        if (denial is not null)
                            return transaction.Abort(denial);

                        var reservation = QuotaDomainFactory.CreateReservation(
                            tenantId,
                            delta,
                            resourceKey,
                            nowUtc,
                            settings.ActiveReservationLifetime,
                            settings.FinalizedReservationHistory);
                        await reservations.InsertOneAsync(transaction.Session, reservation, cancellationToken: ct);

                        var activeState = (ActiveQuotaReservationState)reservation.State;
                        return transaction.Commit(
                            new QuotaReservationLease(
                                reservation.Id,
                                reservation.TenantId,
                                QuotaDelta.Create(reservation.Delta),
                                activeState.ExpiresAtUtc,
                                reservation.ResourceKey));
                    },
                    cancellationToken);
            }
            catch (MongoWriteException exception) when (exception.WriteError?.Category ==
                                                        ServerErrorCategory.DuplicateKey &&
                                                        attempt < InitialUpsertRetryCount - 1)
            {
                // The first admission for a tenant can race to create the usage row.
            }

        return new QuotaReservationConflict(tenantId, resourceKey);
    }

    private static QuotaExceeded? GetAdmissionDenial(
        TenantQuotaPolicy? policy,
        TenantQuotaUsage usage,
        IReadOnlyCollection<QuotaReservation> activeReservations,
        QuotaDelta requestedDelta)
    {
        if (policy is null)
            return null;

        var activeValues = QuotaValues.Sum(activeReservations.Select(reservation => reservation.Delta));
        foreach (var limit in policy.Limits)
        {
            var current = checked(QuotaValues.GetValue(usage.Values, limit.Key) +
                QuotaValues.GetValue(activeValues, limit.Key));
            var requested = requestedDelta[limit.Key];
            var projected = checked(current + requested);
            if (projected > limit.Value)
                return new QuotaExceeded(limit.Key, limit.Value, requested, current);
        }

        return null;
    }

    private static void ValidateRequest(
        string tenantId,
        QuotaDelta delta,
        QuotaResourceKey resourceKey)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        if (delta.IsEmpty)
            throw new ArgumentException("Quota delta must contain at least one quota.", nameof(delta));
        if (delta.Values.Any(value => value.Value <= 0))
            throw new ArgumentException("Reserved quota values must be greater than zero.", nameof(delta));
        if (string.IsNullOrWhiteSpace(resourceKey.Value))
            throw new ArgumentException("Quota resource key is required.", nameof(resourceKey));
    }
}
