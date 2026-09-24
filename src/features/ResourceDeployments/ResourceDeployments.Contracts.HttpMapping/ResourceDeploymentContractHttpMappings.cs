using System.Net;
using Pole.ASP.Helpers;

namespace Pole.Features.ResourceDeployments.Contracts.HttpMapping;

public static class ResourceDeploymentContractHttpMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } =
        Array.AsReadOnly<HttpProblemMapping>(
        [
            HttpProblemMapping.For<CreateResourceDeploymentError>(MapDeploymentError),
            HttpProblemMapping.For<ResourceDeploymentMutationError>(MapMutationError),
            HttpProblemMapping.For<LoadCurrentResourceDeploymentComputeError>(MapComputeError),
            HttpProblemMapping.For<ResourceGroupHasUnfinishedDeployments>(error => Problem(
                HttpStatusCode.Conflict,
                "resource-deployments.resource-group-referenced",
                "Resource group has unfinished resource deployments",
                ("resourceGroupId", error.ResourceGroupId))),
            HttpProblemMapping.For<IsolationScopeHasUnfinishedDeployments>(error => Problem(
                HttpStatusCode.Conflict,
                "resource-deployments.isolation-scope-referenced",
                "Isolation scope has unfinished resource deployments",
                ("resourceGroupId", error.ResourceGroupId),
                ("isolationScopeId", error.IsolationScopeId))),
            HttpProblemMapping.For<ComputeProfileReferencedByResourceDeployment>(MapComputeProfileReferenced),
            HttpProblemMapping.For<ResourceDeploymentFinalizationConflict>(error => Problem(
                HttpStatusCode.Conflict,
                "resource-deployments.finalization-conflict",
                "Resource deployment is not ready for finalization",
                ("resourceId", error.ResourceId))),
            HttpProblemMapping.For<AdvanceResourceDeploymentRevisionError>(MapRevisionError)
        ]);

    public static HttpProblem MapComputeProfileReferenced(
        ComputeProfileReferencedByResourceDeployment error) => Problem(
                HttpStatusCode.Conflict,
                "resource-deployments.compute-profile-referenced",
                "Compute profile is referenced by a resource deployment",
                ("computeProfileId", error.ComputeProfileId));

    private static HttpProblem MapDeploymentError(
        CreateResourceDeploymentError error) => error switch
        {
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

    private static HttpProblem MapMutationError(
        ResourceDeploymentMutationError error) => error switch
        {
            ResourceDeploymentMutationNotFound notFound => Problem(
                HttpStatusCode.NotFound,
                "resource-deployments.not-found",
                "Resource deployment was not found",
                ("resourceId", notFound.ResourceId)),
            ResourceDeploymentChanged changed => Problem(
                HttpStatusCode.Conflict,
                "resource-deployments.changed",
                "Resource deployment changed concurrently",
                ("resourceId", changed.ResourceId)),
            ResourceDeploymentCardinalityConflict conflict => Problem(
                HttpStatusCode.Conflict,
                "resource-deployments.cardinality-conflict",
                "Resource deployment cardinality is incompatible with this operation",
                ("resourceId", conflict.ResourceId)),
            ResourceDeploymentPendingDeletion pendingDeletion => Problem(
                HttpStatusCode.Conflict,
                "resource-deployments.pending-deletion",
                "Resource deployment is pending deletion",
                ("resourceId", pendingDeletion.ResourceId)),
            ResourceDeploymentPlacementUnavailable unavailable => Problem(
                HttpStatusCode.Conflict,
                "resource-deployments.placement-unavailable",
                "Resource deployment placement is unavailable",
                ("resourceId", unavailable.ResourceId)),
            ResourceDeploymentReplacementTargetMismatch mismatch => Problem(
                HttpStatusCode.Conflict,
                "resource-deployments.replacement-target-mismatch",
                "Replacement compute target does not match the deployment target",
                ("resourceId", mismatch.ResourceId)),
            ResourceDeploymentIngressConfigurationChanged changed => Problem(
                HttpStatusCode.Conflict,
                "resource-deployments.ingress-changed",
                "Resource deployment ingress configuration changed",
                ("resourceId", changed.ResourceId),
                ("resourceDeploymentId", changed.ResourceDeploymentId)),
            ResourceDeploymentRetryUnavailable unavailable => Problem(
                HttpStatusCode.Conflict,
                "resource-deployments.retry-unavailable",
                "Resource deployment operation is not retryable",
                ("resourceId", unavailable.ResourceId),
                ("resourceDeploymentId", unavailable.ResourceDeploymentId)),
            ResourceDeploymentComputeConfigurationChanged changed => Problem(
                HttpStatusCode.Conflict,
                "resource-deployments.compute-profile-changed",
                "Compute profile changed",
                ("computeProfileIds", changed.ComputeProfileIds))
        };

    private static HttpProblem MapComputeError(
        LoadCurrentResourceDeploymentComputeError error) => error switch
        {
            CurrentResourceDeploymentComputeNotFound notFound => Problem(
                HttpStatusCode.NotFound,
                "resource-deployments.not-found",
                "Resource deployment was not found",
                ("resourceId", notFound.ResourceId)),
            CurrentResourceDeploymentComputeCardinalityConflict conflict => Problem(
                HttpStatusCode.Conflict,
                "resource-deployments.compute-cardinality-conflict",
                "Resource does not have exactly one compute configuration",
                ("resourceId", conflict.ResourceId))
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
