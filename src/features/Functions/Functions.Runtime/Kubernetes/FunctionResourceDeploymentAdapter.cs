// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.FunctionApps;
using Pole.Features.Functions.Application.Revisions;
using Pole.Features.Functions.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Workloads;

namespace Pole.Features.Functions.Runtime.Kubernetes;

public sealed class FunctionResourceDeploymentAdapter(
    LoadFunctionAppById loadFunctionAppById,
    LoadFunctionAppRevisions loadFunctionAppRevisions,
    IFunctionRevisionKubernetesManager kubernetesManager,
    ReconcileResourceIngressPolicy reconcileIngressPolicy
) : IResourceDeploymentAdapter
{
    public async Task<ResourceDeploymentAdapterResult> ApplyAsync(
        ResourceDeploymentReconciliationContext context,
        CancellationToken cancellationToken = default)
    {
        var deployment = context.Deployment;
        var app = await loadFunctionAppById(deployment.TenantId, deployment.ResourceId, cancellationToken);
        if (app is null)
            return ResourceDeploymentAdapterResult.Failed.Create(
                "FunctionAppNotFound",
                "The Function App no longer exists.",
                retryable: false);
        if (app.DesiredState == FunctionAppDesiredState.Prepared)
            return ResourceDeploymentAdapterResult.Failed.Create(
                "FunctionConfigurationPending",
                "The Function App is not configured yet.");

        if (!Matches(app, deployment))
            return ResourceDeploymentAdapterResult.Failed.Create(
                "FunctionPlacementMismatch",
                "The Function App placement identity does not match its resource deployment.",
                retryable: false);

        var ingress = await reconcileIngressPolicy(
            PolicyRequest(deployment, [NetworkPort.Create(FunctionRevisionInKubernetes.AgentPort)]),
            cancellationToken);
        if (ingress is not ResourceIngressPolicyApplied)
            return ResourceDeploymentAdapterResult.Failed.Create(
                "FunctionGatewayIngressUnavailable",
                "The Function gateway ingress policy is not ready.");

        // Revision provisioning remains Function-owned. Marking the placement ready here
        // releases the placement-fenced revision event after RuntimeCell reconciliation.
        return ResourceDeploymentAdapterResult.Succeeded.Create();
    }

    public async Task<ResourceDeploymentAdapterResult> DeleteAsync(
        ResourceDeploymentReconciliationContext context,
        CancellationToken cancellationToken = default)
    {
        var deployment = context.Deployment;
        var app = await loadFunctionAppById(deployment.TenantId, deployment.ResourceId, cancellationToken);
        if (app is null)
        {
            var missingAppIngress = await reconcileIngressPolicy(PolicyRequest(deployment, []), cancellationToken);
            return missingAppIngress is ResourceIngressPolicyApplied
                ? ResourceDeploymentAdapterResult.Succeeded.Create()
                : ResourceDeploymentAdapterResult.Failed.Create(
                    "FunctionGatewayIngressCleanupUnavailable",
                    "The Function gateway ingress policy could not be closed.");
        }

        if (!Matches(app, deployment))
            return ResourceDeploymentAdapterResult.Failed.Create(
                "FunctionPlacementMismatch",
                "The Function App placement identity does not match its resource deployment.",
                retryable: false);

        var placement = new FunctionResourcePlacement(
            deployment.Id,
            deployment.RuntimeCellId,
            deployment.Placement.DeploymentTargetId,
            deployment.Placement.KubernetesNamespace,
            deployment.IsolationScopeId);
        var revisions = await loadFunctionAppRevisions(app, cancellationToken);
        foreach (var revision in revisions)
        {
            var result = await kubernetesManager.DeleteAsync(
                new FunctionRevisionInKubernetes(app, revision, placement),
                cancellationToken);
            if (result is FunctionRevisionKubernetesOperationError failure)
                return ResourceDeploymentAdapterResult.Failed.Create("FunctionRevisionDeleteFailed", failure.Message());
        }

        var ingress = await reconcileIngressPolicy(PolicyRequest(deployment, []), cancellationToken);
        if (ingress is not ResourceIngressPolicyApplied)
            return ResourceDeploymentAdapterResult.Failed.Create(
                "FunctionGatewayIngressCleanupUnavailable",
                "The Function gateway ingress policy could not be closed.");

        return ResourceDeploymentAdapterResult.Succeeded.Create();
    }

    private static ResourceIngressPolicyRequest PolicyRequest(
        ResourceDeploymentReconciliationSnapshot deployment,
        ImmutableList<NetworkPort> ports) => new(
        deployment.TenantId,
        deployment.ResourceType,
        deployment.ResourceId,
        deployment.Id,
        deployment.RuntimeCellId,
        deployment.Placement.DeploymentTargetId,
        deployment.Placement.KubernetesNamespace,
        FunctionIngressPolicyDefinition.Purpose,
        deployment.DesiredGeneration,
        deployment.Placement.TargetWorkloadConfigurationRevision,
        ports);

    private static bool Matches(FunctionApp app, ResourceDeploymentReconciliationSnapshot deployment) =>
        string.Equals(app.Placement.ResourceDeploymentId, deployment.Id, StringComparison.Ordinal) &&
        string.Equals(app.Placement.RuntimeCellId, deployment.RuntimeCellId, StringComparison.Ordinal) && string.Equals(
            app.Placement.DeploymentTargetId,
            deployment.Placement.DeploymentTargetId,
            StringComparison.Ordinal);
}

public static class FunctionResourceDeploymentAdapterRegistration
{
    public static ResourceDeploymentAdapterDescriptor Descriptor { get; }
        = ResourceDeploymentAdapterDescriptor.Create<FunctionResourceDeploymentAdapter>(
            FunctionResourceDeployment.ResourceType,
            ResourcePlacementCapability.SingleTarget);
}
