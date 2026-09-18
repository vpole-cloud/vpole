using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Persistence.Reads;
using Pole.Features.Functions.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.ResultType;
using Pole.Workloads;

namespace Pole.Features.Functions.Runtime.Kubernetes;

public sealed class FunctionResourceDeploymentAdapter(
    LoadFunctionAppById loadFunctionAppById,
    LoadFunctionAppRevisions loadFunctionAppRevisions,
    IFunctionRevisionKubernetesManager kubernetesManager)
    : IResourceDeploymentAdapter
{
    public async Task<ResourceDeploymentAdapterResult> ApplyAsync(
        ResourceDeploymentReconciliationContext context,
        CancellationToken cancellationToken = default)
    {
        var deployment = context.Deployment;
        var app = await loadFunctionAppById(
            deployment.TenantId,
            deployment.ResourceId,
            cancellationToken);
        if (app is null)
        {
            return ResourceDeploymentAdapterResult.Failed.Create(
                "FunctionAppNotFound",
                "The Function App no longer exists.",
                retryable: false);
        }
        if (!Matches(app, deployment))
        {
            return ResourceDeploymentAdapterResult.Failed.Create(
                "FunctionPlacementMismatch",
                "The Function App placement identity does not match its resource deployment.",
                retryable: false);
        }

        // Revision provisioning remains Function-owned. Marking the placement ready here
        // releases the placement-fenced revision event after RuntimeCell reconciliation.
        return ResourceDeploymentAdapterResult.Succeeded.Create();
    }

    public async Task<ResourceDeploymentAdapterResult> DeleteAsync(
        ResourceDeploymentReconciliationContext context,
        CancellationToken cancellationToken = default)
    {
        var deployment = context.Deployment;
        var app = await loadFunctionAppById(
            deployment.TenantId,
            deployment.ResourceId,
            cancellationToken);
        if (app is null)
            return ResourceDeploymentAdapterResult.Succeeded.Create();
        if (!Matches(app, deployment))
        {
            return ResourceDeploymentAdapterResult.Failed.Create(
                "FunctionPlacementMismatch",
                "The Function App placement identity does not match its resource deployment.",
                retryable: false);
        }

        var placement = new FunctionResourcePlacement(
            deployment.Id,
            deployment.RuntimeCellId,
            deployment.Placement.DeploymentTargetId,
            deployment.Placement.KubernetesNamespace, deployment.IsolationScopeId);
        var revisions = await loadFunctionAppRevisions(app, cancellationToken);
        foreach (var revision in revisions)
        {
            var result = await kubernetesManager.DeleteAsync(
                new FunctionRevisionInKubernetes(app, revision, placement),
                cancellationToken);
            if (!result.TryGetValue(out _, out var failure))
            {
                return ResourceDeploymentAdapterResult.Failed.Create(
                    "FunctionRevisionDeleteFailed",
                    failure.Message());
            }
        }

        return ResourceDeploymentAdapterResult.Succeeded.Create();
    }

    private static bool Matches(
        FunctionApp app,
        ResourceDeploymentReconciliationSnapshot deployment) =>
        string.Equals(
            app.Placement.ResourceDeploymentId,
            deployment.Id,
            StringComparison.Ordinal) &&
        string.Equals(
            app.Placement.RuntimeCellId,
            deployment.RuntimeCellId,
            StringComparison.Ordinal) &&
        string.Equals(
            app.Placement.DeploymentTargetId,
            deployment.Placement.DeploymentTargetId,
            StringComparison.Ordinal);
}

public static class FunctionResourceDeploymentAdapterRegistration
{
    public static ResourceDeploymentAdapterDescriptor Descriptor { get; } =
        ResourceDeploymentAdapterDescriptor.Create<FunctionResourceDeploymentAdapter>(
            FunctionResourceDeployment.ResourceType,
            ResourcePlacementCapability.SingleTarget);
}
