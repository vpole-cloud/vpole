using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Pole.ASP.Helpers;
using Pole.Features.ResourceGroups.Contracts;

namespace Pole.Features.ResourceGroups.Application.Common;

public static class ResourceGroupHttpMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } =
        Array.AsReadOnly<HttpProblemMapping>(
        [
            HttpProblemMapping.For<ResourceGroupOwnershipError>(MapOwnershipError)
        ]);

    public static IServiceCollection AddResourceGroupHttpMappings(
        this IServiceCollection services) =>
        services.AddHttpProblemMappings(All);

    private static HttpProblem MapOwnershipError(
        ResourceGroupOwnershipError error) => error switch
        {
            ResourceGroupOwnershipNotFound notFound => Problem(
                HttpStatusCode.NotFound,
                "resource-groups.not-found",
                "Resource group not found",
                ("resourceGroupId", notFound.ResourceGroupId)),
            ResourceGroupOwnershipDeleting deleting => Problem(
                HttpStatusCode.Conflict,
                "resource-groups.deleting",
                "Resource group is being deleted",
                ("resourceGroupId", deleting.ResourceGroupId))
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
