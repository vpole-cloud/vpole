using System.Net;
using Pole.ASP.Helpers;

namespace Pole.Features.ResourceDeployments.Contracts.HttpMapping;

public static class ResourceDeploymentContractHttpMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } =
        Array.AsReadOnly<HttpProblemMapping>(
        [
            HttpProblemMapping.For<CreateResourceDeploymentSetError>(MapDeploymentSetError),
            HttpProblemMapping.For<AdvanceResourceDeploymentRevisionError>(MapRevisionError)
        ]);

    private static HttpProblem MapDeploymentSetError(
        CreateResourceDeploymentSetError error) => error switch
        {
            InvalidResourceDeploymentSet invalid => Problem(
                HttpStatusCode.BadRequest,
                "resource-deployments.set.invalid",
                "Resource deployment set is invalid",
                ("errors", invalid.Errors)),
            ResourceDeploymentTargetConfigurationChanged changed => Problem(
                HttpStatusCode.Conflict,
                "resource-deployments.target-changed",
                "Deployment target configuration changed",
                ("deploymentTargetIds", changed.DeploymentTargetIds)),
            ResourceDeploymentComputeConfigurationChanged changed => Problem(
                HttpStatusCode.Conflict,
                "resource-deployments.compute-profile-changed",
                "Compute profile changed",
                ("computeProfileIds", changed.ComputeProfileIds)),
            ResourceDeploymentStorageConfigurationChanged changed => Problem(
                HttpStatusCode.Conflict,
                "resource-deployments.storage-profile-changed",
                "Storage profile changed",
                ("storageProfileId", changed.StorageProfileId)),
            ResourcePlacementResourceGroupNotFound notFound => Problem(
                HttpStatusCode.NotFound,
                "resource-groups.not-found",
                "Resource group not found",
                ("resourceGroupId", notFound.ResourceGroupId)),
            ResourcePlacementUnavailable unavailable => Problem(
                HttpStatusCode.Conflict,
                "resource-deployments.placement-unavailable",
                "Resource placement is unavailable",
                ("resourceGroupId", unavailable.ResourceGroupId)),
            ResourcePlacementDeleting deleting => Problem(
                HttpStatusCode.Conflict,
                "resource-deployments.placement-deleting",
                "Resource placement is being deleted",
                ("deploymentTargetId", deleting.DeploymentTargetId))
        };

    private static HttpProblem MapRevisionError(
        AdvanceResourceDeploymentRevisionError error) => error switch
        {
            ResourceDeploymentsNotFound notFound => Problem(
                HttpStatusCode.NotFound,
                "resource-deployments.not-found",
                "Resource deployments were not found",
                ("resourceId", notFound.ResourceId)),
            ResourceDeploymentsHaveNoActivePlacement unavailable => Problem(
                HttpStatusCode.Conflict,
                "resource-deployments.no-active-placement",
                "Resource has no active deployments",
                ("resourceId", unavailable.ResourceId)),
            ResourceDeploymentRevisionConflict conflict => Problem(
                HttpStatusCode.Conflict,
                "resource-deployments.revision-conflict",
                "Resource deployment changed concurrently",
                ("resourceId", conflict.ResourceId))
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
