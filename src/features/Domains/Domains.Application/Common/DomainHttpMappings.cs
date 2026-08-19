using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Pole.ASP.Helpers;
using Pole.Features.Domains.Application.Persistence;

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
                MapDeleteManagedDomainZoneError)
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
