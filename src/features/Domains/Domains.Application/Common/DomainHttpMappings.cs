using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Pole.ASP.Helpers;
using Pole.Features.Domains.Application.DomainZones;
using Pole.Features.Domains.Contracts;

namespace Pole.Features.Domains.Application.Common;

public static class DomainHttpMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } = Array.AsReadOnly<HttpProblemMapping>(
    [
        HttpProblemMapping.For<CustomHostnameBindingError>(MapCustomHostnameBindingError),
    ]);

    public static IServiceCollection AddDomainHttpMappings(this IServiceCollection services) =>
        services.AddHttpProblemMappings(All);

    public static HttpProblem MapManagedDomainZoneNameConflict(ManagedDomainZoneNameConflict conflict) => Problem(
        HttpStatusCode.Conflict,
        "Domain zone technical name is already used",
        "domains.zone.name-conflict",
        new Dictionary<string, object> { ["technicalName"] = conflict.TechnicalName });

    public static HttpProblem MapManagedDomainZoneAlreadyReserved(ManagedDomainZoneAlreadyReserved reserved) => Problem(
        HttpStatusCode.Conflict,
        "Domain zone is already reserved",
        "domains.zone.already-reserved",
        new Dictionary<string, object>
        {
            ["domain"] = reserved.Domain,
        });

    public static HttpProblem
        MapManagedDomainZoneDeploymentTargetChanged(ManagedDomainZoneDeploymentTargetChanged changed) => Problem(
        HttpStatusCode.Conflict,
        "Deployment target changed during reservation",
        "domains.zone.deployment-target-changed",
        new Dictionary<string, object>
        {
            ["deploymentTargetId"] = changed.DeploymentTargetId,
        });

    public static HttpProblem MapManagedDomainZoneReferenceRejected(ManagedDomainZoneReferenceRejected rejected) =>
        Problem(
            HttpStatusCode.Conflict,
            "Deployment target rejected the domain reservation",
            "domains.zone.deployment-target-reference-rejected",
            new Dictionary<string, object>
            {
                ["deploymentTargetId"] = rejected.DeploymentTargetId,
            });

    public static HttpProblem MapGeneratedDomainZoneDeletionRejected(GeneratedDomainZoneDeletionRejected generated) =>
        Problem(
            HttpStatusCode.Conflict,
            "Generated domain zone cannot be deleted",
            "domains.zone.delete.generated",
            new Dictionary<string, object>
            {
                ["domainZoneId"] = generated.ZoneId,
            });

    public static HttpProblem MapDomainZoneHasHostnameBindings(DomainZoneHasHostnameBindings referenced) => Problem(
        HttpStatusCode.Conflict,
        "Domain zone still has hostname bindings",
        "domains.zone.delete.has-hostname-bindings",
        new Dictionary<string, object>
        {
            ["domainZoneId"] = referenced.ZoneId,
            ["bindingCount"] = referenced.BindingCount,
        });

    public static HttpProblem MapDomainZoneGitOwned(DomainZoneGitOwned owned) => Problem(
        HttpStatusCode.Conflict,
        "Domain zone configuration is managed by GitOps",
        "domains.zone.git-owned",
        new Dictionary<string, object> { ["domainZoneId"] = owned.ZoneId });

    public static HttpProblem MapDomainZoneDeletionConflict(DomainZoneDeletionConflict conflict) => Problem(
        HttpStatusCode.Conflict,
        "Domain zone changed during deletion",
        "domains.zone.delete.conflict",
        new Dictionary<string, object>
        {
            ["domainZoneId"] = conflict.ZoneId,
        });

    public static HttpProblem
        MapManagedDomainZoneReferenceRemovalRejected(ManagedDomainZoneReferenceRemovalRejected rejected) => Problem(
        HttpStatusCode.Conflict,
        "Deployment target rejected domain zone deletion",
        "domains.zone.delete.reference-removal-rejected",
        new Dictionary<string, object>
        {
            ["domainZoneId"] = rejected.ZoneId,
            ["deploymentTargetId"] = rejected.DeploymentTargetId,
        });

    public static HttpProblem MapDomainZoneNotFound(DomainZoneNotFound notFound) => Problem(
        HttpStatusCode.NotFound,
        "Domain zone not found",
        "domains.zone.not-found",
        new Dictionary<string, object>
        {
            ["domainZoneId"] = notFound.ZoneId,
        });

    public static HttpProblem MapCustomHostnameBindingError(CustomHostnameBindingError error) => error switch
    {
        CustomHostnameBindingInvalidRelativeName => Problem(
            HttpStatusCode.BadRequest,
            "The relative name does not form a valid hostname in this zone",
            "domains.hostname.invalid-name"),
        CustomHostnameBindingZoneNotFound notFound => Problem(
            HttpStatusCode.NotFound,
            "Domain zone not found",
            "domains.hostname.zone-not-found",
            new Dictionary<string, object>
            {
                ["domainZoneId"] = notFound.TenantDomainZoneId,
            }),
        CustomHostnameBindingTargetMismatch mismatch => Problem(
            HttpStatusCode.Conflict,
            "Domain zone belongs to another deployment target",
            "domains.hostname.zone-target-mismatch",
            new Dictionary<string, object>
            {
                ["domainZoneId"] = mismatch.TenantDomainZoneId,
                ["deploymentTargetId"] = mismatch.DeploymentTargetId,
            }),
        CustomHostnameBindingNameConflict conflict => Problem(
            HttpStatusCode.Conflict,
            "Hostname binding technical name is already used",
            "domains.hostname.name-conflict",
            new Dictionary<string, object> { ["technicalName"] = conflict.TechnicalName }),
        CustomHostnameBindingConflict conflict => Problem(
            HttpStatusCode.Conflict,
            "Hostname is already bound",
            "domains.hostname.already-bound",
            new Dictionary<string, object>
            {
                ["hostname"] = conflict.Hostname,
            }),
        CustomHostnameBindingNotFound notFound => Problem(
            HttpStatusCode.NotFound,
            "Hostname binding not found",
            "domains.hostname.not-found",
            new Dictionary<string, object>
            {
                ["bindingId"] = notFound.BindingId,
            }),
        GeneratedHostnameBindingDeletionRejected rejected => Problem(
            HttpStatusCode.Conflict,
            "Generated hostname binding cannot be deleted",
            "domains.hostname.delete.generated",
            new Dictionary<string, object>
            {
                ["bindingId"] = rejected.BindingId,
            }),
        CustomHostnameBindingZoneUnavailable unavailable => Problem(
            HttpStatusCode.Conflict,
            "Hostname binding domain zone is unavailable",
            "domains.hostname.zone-unavailable",
            new Dictionary<string, object>
            {
                ["domainZoneId"] = unavailable.TenantDomainZoneId,
            }),
        CustomHostnameBindingZoneChanged changed => Problem(
            HttpStatusCode.Conflict,
            "Domain zone changed during hostname update",
            "domains.hostname.zone-changed",
            new Dictionary<string, object>
            {
                ["domainZoneIds"] = changed.TenantDomainZoneIds,
            }),
        CustomHostnameBindingDeletionConflict conflict => Problem(
            HttpStatusCode.Conflict,
            "Hostname binding changed during deletion",
            "domains.hostname.delete.conflict",
            new Dictionary<string, object>
            {
                ["bindingId"] = conflict.BindingId,
            }),
    };

    private static HttpProblem Problem(
        HttpStatusCode statusCode,
        string title,
        string code,
        IReadOnlyDictionary<string, object>? extensions = null,
        string? detail = null) => new(
        (int)statusCode,
        PoleProblemTypeId.FromCode(code),
        title,
        code,
        detail,
        extensions);
}
