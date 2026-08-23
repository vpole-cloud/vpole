using MongoDB.Driver;
using Pole.Features.Quotas.Domain;

namespace Pole.Features.Quotas.Application.Persistence;

internal static class QuotaReservationFields
{
    public const string StateKind = "State.Kind";
    public const string StateExpiresAtUtc = "State.ExpiresAtUtc";
}

internal static class QuotaReservationFilters
{
    public static FilterDefinition<QuotaReservation> ById(string reservationId) =>
        Builders<QuotaReservation>.Filter.Eq(reservation => reservation.Id, reservationId);

    public static FilterDefinition<QuotaReservation> ActiveForTenant(
        string tenantId,
        DateTime nowUtc) =>
        Builders<QuotaReservation>.Filter.And(
            Builders<QuotaReservation>.Filter.Eq(
                reservation => reservation.TenantId,
                tenantId),
            Active(nowUtc));

    public static FilterDefinition<QuotaReservation> Active(string reservationId) =>
        Builders<QuotaReservation>.Filter.And(
            ById(reservationId),
            Builders<QuotaReservation>.Filter.Eq(
                QuotaReservationFields.StateKind,
                QuotaReservationStateKinds.Active));

    public static FilterDefinition<QuotaReservation> Active(
        string reservationId,
        DateTime nowUtc) =>
        Builders<QuotaReservation>.Filter.And(
            Active(reservationId),
            Builders<QuotaReservation>.Filter.Gt(
                QuotaReservationFields.StateExpiresAtUtc,
                nowUtc));

    private static FilterDefinition<QuotaReservation> Active(DateTime nowUtc) =>
        Builders<QuotaReservation>.Filter.And(
            Builders<QuotaReservation>.Filter.Eq(
                QuotaReservationFields.StateKind,
                QuotaReservationStateKinds.Active),
            Builders<QuotaReservation>.Filter.Gt(
                QuotaReservationFields.StateExpiresAtUtc,
                nowUtc));
}
