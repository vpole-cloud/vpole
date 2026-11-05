using System.Net;
using Pole.ASP.Helpers;
using Pole.Features.Tenants.Application.Members;

namespace Pole.Features.Tenants.Application;

public static class TenantMemberAuthorizationHttpProblemMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } =
        Array.AsReadOnly<HttpProblemMapping>(
        [
            HttpProblemMapping.For<TenantAuthenticationRequired>(
                MapAuthenticationRequired),
            HttpProblemMapping.For<TenantUserIdentityRequired>(
                MapUserIdentityRequired),
            HttpProblemMapping.For<TenantNotFound>(MapTenantNotFound),
            HttpProblemMapping.For<TenantMembershipRequired>(
                MapMembershipRequired),
            HttpProblemMapping.For<TenantMemberNotFound>(MapMemberNotFound)
        ]);

    public static HttpProblem MapAuthenticationRequired(
        TenantAuthenticationRequired _) =>
        Problem(
            HttpStatusCode.Unauthorized,
            "Authentication required",
            "tenant.authorization.authentication-required",
            "An authenticated user is required.");

    public static HttpProblem MapUserIdentityRequired(
        TenantUserIdentityRequired _) =>
        Problem(
            HttpStatusCode.Unauthorized,
            "User identity required",
            "tenant.authorization.user-identity-required",
            "The authenticated user id claim is required.");

    public static HttpProblem MapTenantNotFound(
        TenantNotFound notFound) =>
        Problem(
            HttpStatusCode.NotFound,
            "Tenant not found",
            "tenants.not-found",
            extensions: new Dictionary<string, object>
            {
                ["tenantId"] = notFound.TenantId
            });

    public static HttpProblem MapMembershipRequired(
        TenantMembershipRequired required) =>
        Problem(
            HttpStatusCode.Forbidden,
            "Tenant membership required",
            "tenant.authorization.membership-required",
            "Tenant membership is required.",
            new Dictionary<string, object>
            {
                ["tenantId"] = required.TenantId
            });

    public static HttpProblem MapMemberNotFound(
        TenantMemberNotFound notFound) =>
        Problem(
            HttpStatusCode.NotFound,
            "Tenant member not found",
            "tenants.member.not-found",
            extensions: new Dictionary<string, object>
            {
                ["tenantId"] = notFound.TenantId,
                ["memberId"] = notFound.MemberId
            });

    public static HttpProblem MapMemberAlreadyExists(
        TenantMemberAlreadyExists conflict) =>
        Problem(
            HttpStatusCode.Conflict,
            "Tenant member already exists",
            "tenants.member.already-exists",
            extensions: new Dictionary<string, object>
            {
                ["tenantId"] = conflict.TenantId,
                ["userId"] = conflict.UserId
            });

    public static HttpProblem MapLastOwner(
        TenantLastOwner conflict) =>
        Problem(
            HttpStatusCode.Conflict,
            "Tenant must keep an owner",
            "tenants.member.last-owner",
            "A tenant must retain at least one owner.",
            new Dictionary<string, object>
            {
                ["tenantId"] = conflict.TenantId,
                ["memberId"] = conflict.MemberId
            });

    private static HttpProblem Problem(
        HttpStatusCode statusCode,
        string title,
        string code,
        string? detail = null,
        IReadOnlyDictionary<string, object>? extensions = null) =>
        new(
            (int)statusCode,
            PoleProblemTypeId.FromCode(code),
            title,
            code,
            detail,
            extensions);
}
