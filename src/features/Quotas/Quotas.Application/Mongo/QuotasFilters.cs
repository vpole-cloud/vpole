// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.Quotas.Domain;

namespace Pole.Features.Quotas.Application.Mongo;

internal static class QuotaPolicyFilters
{
    public static FilterDefinition<TenantQuotaPolicy> ByTenant(string tenantId) =>
        Builders<TenantQuotaPolicy>.Filter.Eq(policy => policy.TenantId, tenantId);
}

internal static class QuotaReservationFilters
{
    public static FilterDefinition<QuotaReservation> ById(string reservationId) =>
        Builders<QuotaReservation>.Filter.Eq(reservation => reservation.Id, reservationId);

    public static FilterDefinition<QuotaReservation> ActiveForTenant(string tenantId, DateTime nowUtc) =>
        Builders<QuotaReservation>.Filter.And(
            Builders<QuotaReservation>.Filter.Eq(reservation => reservation.TenantId, tenantId),
            Active(nowUtc));

    public static FilterDefinition<QuotaReservation> Active(string reservationId) =>
        Builders<QuotaReservation>.Filter.And(
            ById(reservationId),
            Builders<QuotaReservation>.Filter.Eq(
                reservation => reservation.State.Kind,
                QuotaReservationStateKinds.Active));

    public static FilterDefinition<QuotaReservation> Active(string reservationId, DateTime nowUtc) =>
        Builders<QuotaReservation>.Filter.And(
            Active(reservationId),
            Builders<QuotaReservation>.Filter.Gt(
                reservation => ((ActiveQuotaReservationState)reservation.State).ExpiresAtUtc,
                nowUtc));

    private static FilterDefinition<QuotaReservation> Active(DateTime nowUtc) => Builders<QuotaReservation>.Filter.And(
        Builders<QuotaReservation>.Filter.Eq(reservation => reservation.State.Kind, QuotaReservationStateKinds.Active),
        Builders<QuotaReservation>.Filter.Gt(
            reservation => ((ActiveQuotaReservationState)reservation.State).ExpiresAtUtc,
            nowUtc));
}

internal static class QuotaUsageFilters
{
    public static FilterDefinition<TenantQuotaUsage> ByTenant(string tenantId) =>
        Builders<TenantQuotaUsage>.Filter.Eq(usage => usage.TenantId, tenantId);
}
