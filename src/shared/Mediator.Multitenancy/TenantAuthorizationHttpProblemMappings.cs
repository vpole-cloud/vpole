using System.Net;
using Pole.ASP.Helpers;

namespace Pole.Mediator;

public static class TenantAuthorizationHttpProblemMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } = Array.AsReadOnly<HttpProblemMapping>(
    [
        HttpProblemMapping.For<TenantContextRequired>(_ => Problem(
            "Active tenant context required",
            "tenant.authorization.context-required",
            "The active tenant must be resolved before executing a tenant operation.")),
        HttpProblemMapping.For<TenantContextMismatch>(_ => Problem(
            "Tenant context mismatch",
            "tenant.authorization.context-mismatch",
            "The operation tenant must match the active tenant.")),
        HttpProblemMapping.For<TenantPlatformAdminRequired>(MapPlatformAdminRequired),
        HttpProblemMapping.For<TenantRoleRequired>(MapTenantRoleRequired),
    ]);

    private static HttpProblem MapPlatformAdminRequired(TenantPlatformAdminRequired _) => Problem(
        "Platform administrator role required",
        "tenant.authorization.platform-admin-required",
        "Platform admin role is required for tenant-wide operations.");

    private static HttpProblem MapTenantRoleRequired(TenantRoleRequired required) => Problem(
        "Tenant role required",
        "tenant.authorization.role-required",
        $"The '{required.MinimumRole}' tenant role or higher is required.",
        new Dictionary<string, object>
        {
            ["minimumRole"] = required.MinimumRole.ToString().ToLowerInvariant(),
        });

    private static HttpProblem Problem(
        string title,
        string code,
        string detail,
        IReadOnlyDictionary<string, object>? extensions = null) => new(
        (int)HttpStatusCode.Forbidden,
        PoleProblemTypeId.FromCode(code),
        title,
        code,
        detail,
        extensions);
}
