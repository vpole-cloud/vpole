using System.Net;
using Pole.ASP.Helpers;

namespace Pole.Features.ResourceGroups.Contracts.HttpMapping;

public static class ResourceGroupContractHttpMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } =
        Array.AsReadOnly<HttpProblemMapping>(
        [HttpProblemMapping.For<ResourceRegistrationMutationError>(MapRegistrationMutationError)]);

    public static HttpProblem MapRegistrationMutationError(
        ResourceRegistrationMutationError error) => error switch
        {
            ResourceRegistrationResourceGroupNotFound notFound =>
                MapRegistrationResourceGroupNotFound(notFound),
            ResourceRegistrationResourceGroupDeleting deleting =>
                MapRegistrationResourceGroupDeleting(deleting),
            ResourceRegistrationIdentityConflict conflict =>
                MapRegistrationIdentityConflict(conflict),
            ResourceRegistrationNotFound notFound =>
                MapRegistrationNotFound(notFound)
        };

    public static HttpProblem MapRegistrationResourceGroupNotFound(
        ResourceRegistrationResourceGroupNotFound notFound) => Problem(
        HttpStatusCode.NotFound,
        "resource-groups.not-found",
        "Resource group not found",
        ("resourceGroupId", notFound.ResourceGroupId));

    public static HttpProblem MapRegistrationResourceGroupDeleting(
        ResourceRegistrationResourceGroupDeleting deleting) => Problem(
        HttpStatusCode.Conflict,
        "resource-groups.deleting",
        "Resource group is being deleted",
        ("resourceGroupId", deleting.ResourceGroupId));

    public static HttpProblem MapRegistrationIdentityConflict(
        ResourceRegistrationIdentityConflict conflict) => Problem(
        HttpStatusCode.Conflict,
        "resource-groups.resource-registration.identity-conflict",
        "Resource identity is already registered",
        ("resourceTypeId", conflict.ResourceTypeId.Value),
        ("resourceId", conflict.ResourceId));

    public static HttpProblem MapRegistrationNotFound(
        ResourceRegistrationNotFound notFound) => Problem(
        HttpStatusCode.NotFound,
        "resource-groups.resource-registration.not-found",
        "Resource registration not found",
        ("resourceTypeId", notFound.ResourceTypeId.Value),
        ("resourceId", notFound.ResourceId));

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
