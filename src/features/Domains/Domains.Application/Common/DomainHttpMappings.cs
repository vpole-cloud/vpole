using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Pole.ASP.Helpers;
using Pole.Features.Domains.Application.Persistence;
using Pole.Features.Domains.Contracts;

namespace Pole.Features.Domains.Application.Common;

public static class DomainHttpMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } =
        Array.AsReadOnly<HttpProblemMapping>(
        [
            HttpProblemMapping.For<DomainZoneNotFound>(MapDomainZoneNotFound),
            HttpProblemMapping.For<CreateManagedDomainZoneError>(
                MapCreateManagedDomainZoneError),
            HttpProblemMapping.For<DeleteManagedDomainZoneError>(
                MapDeleteManagedDomainZoneError),
            HttpProblemMapping.For<CustomHostnameBindingError>(
                MapCustomHostnameBindingError)
        ]);

    public static IServiceCollection AddDomainHttpMappings(
        this IServiceCollection services) =>
        services.AddHttpProblemMappings(All);

    private static HttpProblem MapCreateManagedDomainZoneError(
        CreateManagedDomainZoneError error) => error switch
    {
        InvalidManagedDomainZone invalid => Problem(
            HttpStatusCode.UnprocessableEntity,
            "Domain zone is invalid",
            "domains.zone.invalid",
            detail: invalid.Message),
        ManagedDomainZoneAlreadyReserved reserved => Problem(
            HttpStatusCode.Conflict,
            "Domain zone is already reserved",
            "domains.zone.already-reserved",
            new Dictionary<string, object>
            {
                ["domain"] = reserved.Domain
            }),
        ManagedDomainZoneDeploymentTargetChanged changed => Problem(
            HttpStatusCode.Conflict,
            "Deployment target changed during reservation",
            "domains.zone.deployment-target-changed",
            new Dictionary<string, object>
            {
                ["deploymentTargetId"] = changed.DeploymentTargetId
            }),
        ManagedDomainZoneReferenceRejected rejected => Problem(
            HttpStatusCode.Conflict,
            "Deployment target rejected the domain reservation",
            "domains.zone.deployment-target-reference-rejected",
            new Dictionary<string, object>
            {
                ["deploymentTargetId"] = rejected.DeploymentTargetId
            })
    };

    private static HttpProblem MapDeleteManagedDomainZoneError(
        DeleteManagedDomainZoneError error) => error switch
    {
        DomainZoneNotFound notFound => MapDomainZoneNotFound(notFound),
        GeneratedDomainZoneDeletionRejected generated => Problem(
            HttpStatusCode.Conflict,
            "Generated domain zone cannot be deleted",
            "domains.zone.delete.generated",
            new Dictionary<string, object>
            {
                ["domainZoneId"] = generated.ZoneId
            }),
        DomainZoneHasHostnameBindings referenced => Problem(
            HttpStatusCode.Conflict,
            "Domain zone still has hostname bindings",
            "domains.zone.delete.has-hostname-bindings",
            new Dictionary<string, object>
            {
                ["domainZoneId"] = referenced.ZoneId,
                ["bindingCount"] = referenced.BindingCount
            }),
        DomainZoneDeletionConflict conflict => Problem(
            HttpStatusCode.Conflict,
            "Domain zone changed during deletion",
            "domains.zone.delete.conflict",
            new Dictionary<string, object>
            {
                ["domainZoneId"] = conflict.ZoneId
            }),
        ManagedDomainZoneReferenceRemovalRejected rejected => Problem(
            HttpStatusCode.Conflict,
            "Deployment target rejected domain zone deletion",
            "domains.zone.delete.reference-removal-rejected",
            new Dictionary<string, object>
            {
                ["domainZoneId"] = rejected.ZoneId,
                ["deploymentTargetId"] = rejected.DeploymentTargetId
            })
    };

    private static HttpProblem MapDomainZoneNotFound(
        DomainZoneNotFound notFound) =>
        Problem(
            HttpStatusCode.NotFound,
            "Domain zone not found",
            "domains.zone.not-found",
            new Dictionary<string, object>
            {
                ["domainZoneId"] = notFound.ZoneId
            });

    private static HttpProblem MapCustomHostnameBindingError(
        CustomHostnameBindingError error) => error switch
    {
        CustomHostnameBindingZoneNotFound notFound => Problem(
            HttpStatusCode.NotFound,
            "Domain zone not found",
            "domains.hostname.zone-not-found",
            new Dictionary<string, object>
            {
                ["domainZoneId"] = notFound.TenantDomainZoneId
            }),
        CustomHostnameBindingTargetMismatch mismatch => Problem(
            HttpStatusCode.Conflict,
            "Domain zone belongs to another deployment target",
            "domains.hostname.zone-target-mismatch",
            new Dictionary<string, object>
            {
                ["domainZoneId"] = mismatch.TenantDomainZoneId,
                ["deploymentTargetId"] = mismatch.DeploymentTargetId
            }),
        CustomHostnameBindingConflict conflict => Problem(
            HttpStatusCode.Conflict,
            "Hostname is already bound",
            "domains.hostname.already-bound",
            new Dictionary<string, object>
            {
                ["hostname"] = conflict.Hostname
            }),
        CustomHostnameBindingNotFound notFound => Problem(
            HttpStatusCode.NotFound,
            "Hostname binding not found",
            "domains.hostname.not-found",
            new Dictionary<string, object>
            {
                ["bindingId"] = notFound.BindingId
            }),
        GeneratedHostnameBindingDeletionRejected rejected => Problem(
            HttpStatusCode.Conflict,
            "Generated hostname binding cannot be deleted",
            "domains.hostname.delete.generated",
            new Dictionary<string, object>
            {
                ["bindingId"] = rejected.BindingId
            }),
        CustomHostnameBindingZoneUnavailable unavailable => Problem(
            HttpStatusCode.Conflict,
            "Hostname binding domain zone is unavailable",
            "domains.hostname.zone-unavailable",
            new Dictionary<string, object>
            {
                ["domainZoneId"] = unavailable.TenantDomainZoneId
            }),
        CustomHostnameBindingZoneChanged changed => Problem(
            HttpStatusCode.Conflict,
            "Domain zone changed during hostname update",
            "domains.hostname.zone-changed",
            new Dictionary<string, object>
            {
                ["domainZoneIds"] = changed.TenantDomainZoneIds
            }),
        CustomHostnameBindingDeletionConflict conflict => Problem(
            HttpStatusCode.Conflict,
            "Hostname binding changed during deletion",
            "domains.hostname.delete.conflict",
            new Dictionary<string, object>
            {
                ["bindingId"] = conflict.BindingId
            })
    };

    private static HttpProblem Problem(
        HttpStatusCode statusCode,
        string title,
        string code,
        IReadOnlyDictionary<string, object>? extensions = null,
        string? detail = null) =>
        new(
            (int)statusCode,
            PoleProblemTypeId.FromCode(code),
            title,
            code,
            detail,
            extensions);
}
