// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using Pole.ASP.Helpers;

namespace Pole.Features.Quotas.Contracts.HttpMapping;

public static class QuotaContractHttpMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } = Array.AsReadOnly<HttpProblemMapping>(
    [
        HttpProblemMapping.For<QuotaReservationError>(MapReservationError),
        HttpProblemMapping.For<QuotaReservationCommitError>(MapReservationCommitError),
    ]);

    public static HttpProblem MapExecutionError(QuotaExecutionError error) => error switch
    {
        QuotaReservationError reservation => MapReservationError(reservation),
        QuotaReservationCommitError commit => MapReservationCommitError(commit),
    };

    public static HttpProblem MapReservationError(QuotaReservationError error) => error switch
    {
        QuotaExceeded exceeded => new HttpProblem(
            (int)HttpStatusCode.UnprocessableEntity,
            PoleProblemTypeId.FromCode("quotas.reservation.exceeded"),
            "Quota exceeded",
            "quotas.reservation.exceeded",
            extensions: new Dictionary<string, object>
            {
                ["quota"] = exceeded.Quota.Value,
                ["limit"] = exceeded.Limit,
                ["requested"] = exceeded.Requested,
                ["current"] = exceeded.Current,
            }),
        QuotaReservationConflict conflict => new HttpProblem(
            (int)HttpStatusCode.Conflict,
            PoleProblemTypeId.FromCode("quotas.reservation.conflict"),
            "Quota reservation conflicted with another request",
            "quotas.reservation.conflict",
            extensions: new Dictionary<string, object>
            {
                ["resourceKey"] = conflict.ResourceKey.Value,
            }),
    };

    public static HttpProblem MapReservationCommitError(QuotaReservationCommitError error) => error switch
    {
        QuotaReservationNotFound => Problem(
            HttpStatusCode.Conflict,
            "quotas.reservation.commit-not-found",
            "Quota reservation no longer exists"),
        QuotaReservationCancelled => Problem(
            HttpStatusCode.Conflict,
            "quotas.reservation.commit-cancelled",
            "Quota reservation was cancelled"),
        QuotaReservationExpired => Problem(
            HttpStatusCode.Conflict,
            "quotas.reservation.commit-expired",
            "Quota reservation expired"),
    };

    private static HttpProblem Problem(
        HttpStatusCode status,
        string code,
        string title) => new((int)status, PoleProblemTypeId.FromCode(code), title, code);
}
