using System.Net;
using Pole.ASP.Helpers;

namespace Pole.Features.Domains.Contracts.HttpMapping;

public static class DomainContractHttpMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } =
        Array.AsReadOnly<HttpProblemMapping>(
        [
            HttpProblemMapping.For<GeneratedDomainZoneError>(MapGeneratedZoneError),
            HttpProblemMapping.For<GeneratedHostnameBindingError>(MapGeneratedBindingError)
        ]);

    private static HttpProblem MapGeneratedZoneError(
        GeneratedDomainZoneError error) => error switch
        {
            InvalidGeneratedDomainZoneRequest invalid => Problem(
                HttpStatusCode.BadRequest,
                "domains.generated-zone.invalid",
                "Generated domain zone request is invalid",
                ("errors", invalid.Errors)),
            GeneratedDomainZoneTenantUnavailable unavailable => Problem(
                HttpStatusCode.NotFound,
                "domains.generated-zone.tenant-unavailable",
                "Tenant identity is unavailable",
                ("tenantId", unavailable.TenantId)),
            GeneratedDomainZoneTargetChanged changed => Problem(
                HttpStatusCode.Conflict,
                "domains.generated-zone.target-changed",
                "Deployment target changed while reserving a generated domain zone",
                ("deploymentTargetIds", changed.DeploymentTargetIds)),
            GeneratedDomainZoneMismatch mismatch => Problem(
                HttpStatusCode.Conflict,
                "domains.generated-zone.domain-mismatch",
                "Generated domain zone does not match the target public domain",
                ("deploymentTargetId", mismatch.DeploymentTargetId)),
            GeneratedDomainZoneConflict conflict => Problem(
                HttpStatusCode.Conflict,
                "domains.generated-zone.concurrent-change",
                "Generated domain zone changed concurrently",
                ("deploymentTargetIds", conflict.DeploymentTargetIds))
        };

    private static HttpProblem MapGeneratedBindingError(
        GeneratedHostnameBindingError error) => error switch
        {
            InvalidGeneratedHostnameBindingRequest invalid => Problem(
                HttpStatusCode.BadRequest,
                "domains.generated-binding.invalid",
                "Generated hostname binding request is invalid",
                ("errors", invalid.Errors)),
            GeneratedHostnameBindingZoneNotFound notFound => Problem(
                HttpStatusCode.NotFound,
                "domains.generated-binding.zone-not-found",
                "Domain zone not found",
                ("tenantDomainZoneId", notFound.TenantDomainZoneId)),
            GeneratedHostnameBindingTargetMismatch mismatch => Problem(
                HttpStatusCode.Conflict,
                "domains.generated-binding.target-mismatch",
                "Domain zone belongs to another deployment target",
                ("tenantDomainZoneId", mismatch.TenantDomainZoneId),
                ("deploymentTargetId", mismatch.DeploymentTargetId)),
            GeneratedHostnameBindingZoneChanged changed => Problem(
                HttpStatusCode.Conflict,
                "domains.generated-binding.zone-changed",
                "Domain zone changed concurrently",
                ("tenantDomainZoneIds", changed.TenantDomainZoneIds)),
            GeneratedHostnameBindingConflict conflict => Problem(
                HttpStatusCode.Conflict,
                "domains.generated-binding.conflict",
                "Generated hostname is already reserved",
                ("hostnames", conflict.Hostnames))
        };

    private static HttpProblem Problem(
        HttpStatusCode status,
        string code,
        string title,
        params (string Name, object Value)[] extensions) =>
        new(
            (int)status,
            PoleProblemTypeId.FromCode(code),
            title,
            code,
            extensions: extensions.ToDictionary(
                extension => extension.Name,
                extension => extension.Value));
}
