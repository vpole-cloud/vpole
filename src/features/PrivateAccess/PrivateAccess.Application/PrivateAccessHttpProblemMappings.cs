using System.Net;
using Pole.ASP.Helpers;
using Pole.Features.PrivateAccess.Application.Grants;
using Pole.Features.PrivateAccess.Application.Sessions;
using Pole.Features.PrivateAccess.Contracts;

namespace Pole.Features.PrivateAccess.Application;

public static class PrivateAccessHttpProblemMappings
{
    public static HttpProblem Map(PrivateAccessManageRoleRequired _) => Problem(
        HttpStatusCode.Forbidden,
        "private-access.manage-role-required",
        "Tenant Owner or Admin membership required");

    public static HttpProblem Map(PrivateAccessSubjectNotMember _) => Problem(
        HttpStatusCode.UnprocessableEntity,
        "private-access.subject-not-member",
        "Subject is not a current tenant member");

    public static HttpProblem Map(PrivateAccessResourceUnavailable _) => Problem(
        HttpStatusCode.NotFound,
        "private-access.deployment-unavailable",
        "Deployment is unavailable");

    public static HttpProblem Map(PrivateAccessEndpointUnsupported _) => Problem(
        HttpStatusCode.UnprocessableEntity,
        "private-access.endpoint-unavailable",
        "Endpoint is unavailable");

    public static HttpProblem Map(PrivateAccessGrantExpiryInvalid _) => Problem(
        HttpStatusCode.UnprocessableEntity,
        "private-access.expiry-invalid",
        "Grant expiry must be in the future");

    public static HttpProblem Map(PrivateAccessGrantUnavailable _) => Problem(
        HttpStatusCode.NotFound,
        "private-access.grant-unavailable",
        "Grant is unavailable");

    public static HttpProblem Map(PrivateAccessGrantMissing _) => Problem(
        HttpStatusCode.NotFound,
        "private-access.grant-missing",
        "Grant was not found");

    public static HttpProblem Map(PrivateAccessSessionSubjectMismatch _) => Problem(
        HttpStatusCode.Forbidden,
        "private-access.subject-mismatch",
        "Grant does not belong to the current user");

    public static HttpProblem Map(PrivateAccessSubjectMembershipRequired _) => Problem(
        HttpStatusCode.Forbidden,
        "private-access.membership-required",
        "Current tenant membership required");

    public static HttpProblem Map(PrivateAccessDeploymentUnavailable _) => Problem(
        HttpStatusCode.Conflict,
        "private-access.deployment-unavailable",
        "Deployment is unavailable for private access");

    public static HttpProblem Map(PrivateAccessEndpointUnavailable _) => Problem(
        HttpStatusCode.Conflict,
        "private-access.endpoint-unavailable",
        "Endpoint is unavailable for private access");

    public static HttpProblem Map(PrivateAccessBindingChanged _) => Problem(
        HttpStatusCode.Conflict,
        "private-access.binding-changed",
        "Private access binding changed");

    private static HttpProblem Problem(
        HttpStatusCode status,
        string code,
        string title) => new((int)status, PoleProblemTypeId.FromCode(code), title, code);
}
