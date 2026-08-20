using System.Net;
using Pole.ASP.Helpers;

namespace Pole.Features.Quotas.Contracts.HttpMapping;

public static class QuotaContractHttpMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } =
        Array.AsReadOnly<HttpProblemMapping>(
        [
            HttpProblemMapping.For<QuotaReservationError>(
                MapReservationError),
            HttpProblemMapping.For<QuotaReservationCommitError>(
                MapReservationCommitError),
            HttpProblemMapping.For<InvalidQuotaUsageAdjustment>(
                MapInvalidUsageAdjustment)
        ]);

    private static HttpProblem MapReservationError(
        QuotaReservationError error) => error switch
        {
            InvalidQuotaReservationRequest invalid => new HttpProblem(
                (int)HttpStatusCode.BadRequest,
                PoleProblemTypeId.FromCode("quotas.reservation.invalid"),
                "Quota reservation request is invalid",
                "quotas.reservation.invalid",
                extensions: new Dictionary<string, object>
                {
                    ["errors"] = invalid.Errors
                }),
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
                    ["current"] = exceeded.Current
                }),
            QuotaReservationConflict conflict => new HttpProblem(
                (int)HttpStatusCode.Conflict,
                PoleProblemTypeId.FromCode("quotas.reservation.conflict"),
                "Quota reservation conflicted with another request",
                "quotas.reservation.conflict",
                extensions: new Dictionary<string, object>
                {
                    ["resourceKey"] = conflict.ResourceKey.Value
                })
        };

    private static HttpProblem MapReservationCommitError(
        QuotaReservationCommitError error) => error switch
        {
            InvalidQuotaReservationCommit => Problem(
                HttpStatusCode.InternalServerError,
                "quotas.reservation.commit-invalid",
                "Quota reservation could not be committed"),
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
                "Quota reservation expired")
        };

    private static HttpProblem MapInvalidUsageAdjustment(
        InvalidQuotaUsageAdjustment _) => Problem(
        HttpStatusCode.InternalServerError,
        "quotas.usage.adjustment-invalid",
        "Quota usage could not be adjusted");

    private static HttpProblem Problem(
        HttpStatusCode status,
        string code,
        string title) =>
        new((int)status, PoleProblemTypeId.FromCode(code), title, code);
}
