using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.Quotas.Contracts;
using Pole.Features.Quotas.Domain;
using Pole.ResultType;

namespace Pole.Features.Quotas.Application.Services;

public sealed class MongoQuotaAdmissionService(
    IQuotaDbContext dbContext,
    QuotaSettings settings,
    TimeProvider timeProvider) : IQuotaAdmissionService
{
    private const int InitialUpsertRetryCount = 3;

    public async Task<Result<QuotaReservationLease, QuotaReservationError>> ReserveAsync(
        string tenantId,
        QuotaDelta delta,
        QuotaResourceKey resourceKey,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateReservation(
            tenantId,
            delta,
            resourceKey);
        if (validationError is not null)
        {
            return new Err<QuotaReservationError>(
                new QuotaReservationError(validationError));
        }

        for (var attempt = 0; attempt < InitialUpsertRetryCount; attempt++)
        {
            using var session = await dbContext.Client.StartSessionAsync(cancellationToken: cancellationToken);
            try
            {
                var lease = await session.WithTransactionAsync(
                    async (transactionSession, transactionCancellationToken) =>
                    {
                        var nowUtc = UtcNow();
                        var usage = await TouchUsageAsync(
                            transactionSession,
                            tenantId,
                            nowUtc,
                            transactionCancellationToken);
                        var policy = await dbContext.TenantQuotaPolicies
                            .Find(transactionSession, candidate => candidate.TenantId == tenantId)
                            .FirstOrDefaultAsync(transactionCancellationToken);
                        var activeReservationFilter = Builders<QuotaReservation>.Filter.And(
                            Builders<QuotaReservation>.Filter.Eq(
                                reservation => reservation.TenantId,
                                tenantId),
                            QuotaReservationFilters.Active(nowUtc));
                        var activeReservations = await dbContext.QuotaReservations
                            .Find(transactionSession, activeReservationFilter)
                            .ToListAsync(transactionCancellationToken);

                        var denial = GetAdmissionDenial(
                            policy,
                            usage,
                            activeReservations,
                            delta);
                        if (denial is not null)
                            throw new QuotaAdmissionDeniedException(denial);

                        var reservation = QuotaDomainFactory.CreateReservation(
                            tenantId,
                            delta,
                            resourceKey,
                            nowUtc,
                            settings.ActiveReservationLifetime,
                            settings.FinalizedReservationHistory);

                        await dbContext.QuotaReservations.InsertOneAsync(
                            transactionSession,
                            reservation,
                            cancellationToken: transactionCancellationToken);

                        var activeState = (ActiveQuotaReservationState)reservation.State;
                        return new QuotaReservationLease(
                            reservation.Id,
                            reservation.TenantId,
                            QuotaDelta.Create(reservation.Delta),
                            activeState.ExpiresAtUtc,
                            reservation.ResourceKey);
                    },
                    cancellationToken: cancellationToken);
                return new Ok<QuotaReservationLease>(lease);
            }
            catch (QuotaAdmissionDeniedException exception)
            {
                return new Err<QuotaReservationError>(
                    new QuotaReservationError(exception.Error));
            }
            catch (MongoWriteException exception) when (
                exception.WriteError?.Category == ServerErrorCategory.DuplicateKey &&
                attempt < InitialUpsertRetryCount - 1)
            {
                // The first admission for a tenant can race to upsert the usage row.
            }
        }

        return new Err<QuotaReservationError>(
            new QuotaReservationError(
                new QuotaReservationConflict(tenantId, resourceKey)));
    }

    public async Task<Result> CommitAsync(
        string reservationId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reservationId))
            return ValidationFailure("ReservationId", "Quota reservation id is required.");

        var nowUtc = UtcNow();
        var finalized = await dbContext.QuotaReservations.FindOneAndUpdateAsync(
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
            var existing = await dbContext.QuotaReservations
                .Find(session, reservation => reservation.Id == reservationId)
                .FirstOrDefaultAsync(cancellationToken);

            return existing?.State switch
            {
                null => Failures.NotFound("Quota reservation not found.", reservationId),
                CommittedQuotaReservationState => Result.Success(),
                CancelledQuotaReservationState => Failures.Conflict(
                    "Quota reservation has already been cancelled."),
                ActiveQuotaReservationState => Failures.Conflict(
                    "Quota reservation has expired and cannot be committed."),
                _ => throw new InvalidOperationException("Unknown quota reservation state.")
            };
        }

        var usage = await TouchUsageAsync(
            session,
            finalized.TenantId,
            nowUtc,
            cancellationToken);
        var values = QuotaValues.Add(usage.Values, finalized.Delta);
        await dbContext.TenantQuotaUsages.UpdateOneAsync(
            session,
            candidate => candidate.TenantId == finalized.TenantId,
            Builders<TenantQuotaUsage>.Update
                .Set(candidate => candidate.Values, values)
                .Set(candidate => candidate.UpdatedAtUtc, nowUtc),
            cancellationToken: cancellationToken);

        return Result.Success();
    }

    public async Task<Result> CancelAsync(
        string reservationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reservationId))
            return ValidationFailure("ReservationId", "Quota reservation id is required.");

        var nowUtc = UtcNow();
        var cancelled = await dbContext.QuotaReservations.FindOneAndUpdateAsync(
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
            return Result.Success();

        var existing = await dbContext.QuotaReservations
            .Find(reservation => reservation.Id == reservationId)
            .FirstOrDefaultAsync(cancellationToken);

        return existing?.State switch
        {
            null => Failures.NotFound("Quota reservation not found.", reservationId),
            CancelledQuotaReservationState => Result.Success(),
            CommittedQuotaReservationState => Failures.Conflict(
                "A committed quota reservation cannot be cancelled."),
            _ => throw new InvalidOperationException("Unknown quota reservation state.")
        };
    }

    public async Task<Result> ApplyUsageDeltaAsync(
        string tenantId,
        QuotaDelta delta,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var validationFailure = ValidateTenantAndDelta(
            tenantId,
            delta,
            requireStrictlyPositive: false,
            allowEmpty: true);
        if (validationFailure is not null)
            return validationFailure;

        if (delta.IsEmpty)
            return Result.Success();

        var nowUtc = UtcNow();
        var usage = await TouchUsageAsync(session, tenantId, nowUtc, cancellationToken);
        var values = QuotaValues.AddAndClampToZero(usage.Values, delta.Values);
        await dbContext.TenantQuotaUsages.UpdateOneAsync(
            session,
            candidate => candidate.TenantId == tenantId,
            Builders<TenantQuotaUsage>.Update
                .Set(candidate => candidate.Values, values)
                .Set(candidate => candidate.UpdatedAtUtc, nowUtc),
            cancellationToken: cancellationToken);

        return Result.Success();
    }

    private async Task<TenantQuotaUsage> TouchUsageAsync(
        IClientSessionHandle session,
        string tenantId,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        await dbContext.TenantQuotaUsages.FindOneAndUpdateAsync<TenantQuotaUsage>(
            session,
            candidate => candidate.TenantId == tenantId,
            Builders<TenantQuotaUsage>.Update
                .SetOnInsert(candidate => candidate.Values, ImmutableList<QuotaValue>.Empty)
                .Inc(candidate => candidate.AdmissionRevision, 1)
                .Set(candidate => candidate.UpdatedAtUtc, nowUtc),
            new FindOneAndUpdateOptions<TenantQuotaUsage, TenantQuotaUsage>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After
            },
            cancellationToken);

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
            var current = checked(
                QuotaValues.GetValue(usage.Values, limit.Key) +
                QuotaValues.GetValue(activeValues, limit.Key));
            var requested = requestedDelta[limit.Key];
            var projected = checked(current + requested);
            if (projected > limit.Value)
            {
                return new QuotaExceeded(
                    limit.Key,
                    limit.Value,
                    requested,
                    current);
            }
        }

        return null;
    }

    private static InvalidQuotaReservationRequest? ValidateReservation(
        string tenantId,
        QuotaDelta delta,
        QuotaResourceKey resourceKey)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(tenantId))
            errors["TenantId"] = ["Tenant id is required."];

        if (delta.IsEmpty)
        {
            errors["Delta"] =
                ["Quota delta must contain at least one quota."];
        }
        else if (delta.Values.Any(value => value.Value <= 0))
        {
            errors["Delta"] =
                ["Reserved quota values must be greater than zero."];
        }

        if (string.IsNullOrWhiteSpace(resourceKey.Value))
            errors["ResourceKey"] = ["Quota resource key is required."];

        return errors.Count == 0
            ? null
            : new InvalidQuotaReservationRequest(errors);
    }

    private static Failure? ValidateTenantAndDelta(
        string tenantId,
        QuotaDelta delta,
        bool requireStrictlyPositive,
        bool allowEmpty = false)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return ValidationFailure("TenantId", "Tenant id is required.");

        if (!allowEmpty && delta.IsEmpty)
            return ValidationFailure("Delta", "Quota delta must contain at least one quota.");

        foreach (var value in delta.Values)
        {
            if (requireStrictlyPositive && value.Value <= 0)
                return ValidationFailure("Delta", "Reserved quota values must be greater than zero.");

            if (!requireStrictlyPositive && value.Value >= 0)
                return ValidationFailure("Delta", "Usage-release values must be less than zero.");
        }

        return null;
    }

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    private static ValidationFailure ValidationFailure(string field, string message) =>
        Failures.ValidationFailure(new Dictionary<string, string[]> { [field] = [message] });

    private sealed class QuotaAdmissionDeniedException(QuotaExceeded error)
        : Exception
    {
        public QuotaExceeded Error { get; } = error;
    }
}
