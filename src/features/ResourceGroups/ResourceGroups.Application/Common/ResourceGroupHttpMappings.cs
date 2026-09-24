using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Pole.ASP.Helpers;
using Pole.Features.ResourceGroups.Application.Persistence;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Contracts.HttpMapping;

namespace Pole.Features.ResourceGroups.Application.Common;

public static class ResourceGroupHttpMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } =
        Array.AsReadOnly<HttpProblemMapping>(
        [
            HttpProblemMapping.For<ResourceGroupOwnershipNotFound>(MapResourceGroupOwnershipNotFound),
            HttpProblemMapping.For<ResourceGroupOwnershipDeleting>(MapResourceGroupOwnershipDeleting),
            HttpProblemMapping.For<ResourceRegistrationResourceGroupNotFound>(
                ResourceGroupContractHttpMappings.MapRegistrationResourceGroupNotFound),
            HttpProblemMapping.For<ResourceRegistrationResourceGroupDeleting>(
                ResourceGroupContractHttpMappings.MapRegistrationResourceGroupDeleting),
            HttpProblemMapping.For<ResourceRegistrationIdentityConflict>(
                ResourceGroupContractHttpMappings.MapRegistrationIdentityConflict),
            HttpProblemMapping.For<ResourceRegistrationNotFound>(ResourceGroupContractHttpMappings.MapRegistrationNotFound),
            HttpProblemMapping.For<ResourceGroupNotFound>(MapResourceGroupNotFound),
            HttpProblemMapping.For<ResourceGroupDeleting>(MapResourceGroupDeleting),
            HttpProblemMapping.For<ResourceGroupRevisionConflict>(MapResourceGroupRevisionConflict),
            HttpProblemMapping.For<ResourceGroupContainsResources>(MapResourceGroupContainsResources),
            HttpProblemMapping.For<ResourceGroupDeploymentInProgress>(MapResourceGroupDeploymentInProgress),
            HttpProblemMapping.For<IsolationScopeNotFound>(MapIsolationScopeNotFound),
            HttpProblemMapping.For<IsolationScopeNameConflict>(MapIsolationScopeNameConflict),
            HttpProblemMapping.For<IsolationScopeDeleting>(MapIsolationScopeDeleting),
            HttpProblemMapping.For<DefaultIsolationScopeDeletionForbidden>(
                MapDefaultIsolationScopeDeletionForbidden),
            HttpProblemMapping.For<IsolationScopeReferenced>(error => Problem(
                HttpStatusCode.Conflict,
                "resource-groups.isolation-scopes.referenced",
                "Isolation scope still has resource references",
                ("isolationScopeId", error.IsolationScopeId))),
            HttpProblemMapping.For<IsolationScopeDeploymentInProgress>(
                MapIsolationScopeDeploymentInProgress),
            HttpProblemMapping.For<RuntimeCellNotFound>(MapRuntimeCellNotFound),
            HttpProblemMapping.For<RuntimeCellRetryRejected>(MapRuntimeCellRetryRejected)
        ]);

    public static IServiceCollection AddResourceGroupHttpMappings(
        this IServiceCollection services) =>
        services.AddHttpProblemMappings(All);

    private static HttpProblem MapResourceGroupOwnershipNotFound(
        ResourceGroupOwnershipNotFound notFound) =>
        ResourceGroupNotFoundProblem(notFound.ResourceGroupId);

    private static HttpProblem MapResourceGroupOwnershipDeleting(
        ResourceGroupOwnershipDeleting deleting) =>
        ResourceGroupDeletingProblem(deleting.ResourceGroupId);

    private static HttpProblem MapResourceGroupNotFound(ResourceGroupNotFound notFound) =>
        ResourceGroupNotFoundProblem(notFound.ResourceGroupId);

    private static HttpProblem MapResourceGroupDeleting(ResourceGroupDeleting deleting) =>
        ResourceGroupDeletingProblem(deleting.ResourceGroupId);

    private static HttpProblem MapResourceGroupRevisionConflict(
        ResourceGroupRevisionConflict conflict) => Problem(
        HttpStatusCode.Conflict,
        "resource-groups.revision-conflict",
        "Resource group changed",
        ("resourceGroupId", conflict.ResourceGroupId));

    private static HttpProblem MapResourceGroupContainsResources(
        ResourceGroupContainsResources conflict) => Problem(
        HttpStatusCode.Conflict,
        "resource-groups.contains-resources",
        "Resource group contains resources",
        ("resourceGroupId", conflict.ResourceGroupId));

    private static HttpProblem MapResourceGroupDeploymentInProgress(
        ResourceGroupDeploymentInProgress conflict) => Problem(
        HttpStatusCode.Conflict,
        "resource-groups.deployment-in-progress",
        "Resource group still has unfinished deployments",
        ("resourceGroupId", conflict.ResourceGroupId));

    private static HttpProblem MapIsolationScopeNotFound(IsolationScopeNotFound notFound) => Problem(
        HttpStatusCode.NotFound,
        "resource-groups.isolation-scopes.not-found",
        "Isolation scope not found",
        ("isolationScopeId", notFound.IsolationScopeId));

    private static HttpProblem MapIsolationScopeNameConflict(
        IsolationScopeNameConflict conflict) => Problem(
        HttpStatusCode.Conflict,
        "resource-groups.isolation-scopes.name-conflict",
        "Isolation scope name already exists",
        ("resourceGroupId", conflict.ResourceGroupId),
        ("normalizedName", conflict.NormalizedName));

    private static HttpProblem MapIsolationScopeDeleting(IsolationScopeDeleting deleting) => Problem(
        HttpStatusCode.Conflict,
        "resource-groups.isolation-scopes.deleting",
        "Isolation scope is being deleted",
        ("isolationScopeId", deleting.IsolationScopeId));

    private static HttpProblem MapDefaultIsolationScopeDeletionForbidden(
        DefaultIsolationScopeDeletionForbidden forbidden) => Problem(
        HttpStatusCode.Conflict,
        "resource-groups.isolation-scopes.default-deletion-forbidden",
        "The default isolation scope cannot be deleted separately",
        ("isolationScopeId", forbidden.IsolationScopeId));

    private static HttpProblem MapIsolationScopeDeploymentInProgress(
        IsolationScopeDeploymentInProgress conflict) => Problem(
        HttpStatusCode.Conflict,
        "resource-groups.isolation-scopes.deployment-in-progress",
        "Isolation scope still has unfinished deployments",
        ("isolationScopeId", conflict.IsolationScopeId));

    private static HttpProblem MapRuntimeCellNotFound(RuntimeCellNotFound notFound) => Problem(
        HttpStatusCode.NotFound,
        "resource-groups.runtime-cells.not-found",
        "Runtime cell not found",
        ("runtimeCellId", notFound.RuntimeCellId));

    private static HttpProblem MapRuntimeCellRetryRejected(
        RuntimeCellRetryRejected rejected) => Problem(
        HttpStatusCode.Conflict,
        "resource-groups.runtime-cells.retry-rejected",
        "Runtime cell cannot be retried",
        ("runtimeCellId", rejected.RuntimeCellId));

    private static HttpProblem ResourceGroupNotFoundProblem(string resourceGroupId) => Problem(
        HttpStatusCode.NotFound,
        "resource-groups.not-found",
        "Resource group not found",
        ("resourceGroupId", resourceGroupId));

    private static HttpProblem ResourceGroupDeletingProblem(string resourceGroupId) => Problem(
        HttpStatusCode.Conflict,
        "resource-groups.deleting",
        "Resource group is being deleted",
        ("resourceGroupId", resourceGroupId));

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
