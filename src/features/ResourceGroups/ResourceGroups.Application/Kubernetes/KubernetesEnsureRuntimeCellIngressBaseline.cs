using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceGroups.Application.RuntimeCells;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Kubernetes;

internal sealed class KubernetesEnsureRuntimeCellIngressBaseline(
    RuntimeCellLifecyclePersistence persistence,
    EnsureRuntimeCellNamespace ensureNamespace
)
{
    public async Task<EnsureRuntimeCellIngressBaselineOutcome> InvokeAsync(
        string tenantId,
        string runtimeCellId,
        string deploymentTargetId,
        string kubernetesNamespace,
        CancellationToken cancellationToken = default)
    {
        var cell = await persistence.LoadAsync(tenantId, runtimeCellId, cancellationToken);
        if (cell is null || cell.DeploymentTargetId != deploymentTargetId ||
            cell.KubernetesNamespace != kubernetesNamespace)
            return new RuntimeCellIngressBaselinePlacementMismatch(runtimeCellId);

        if (cell.State is not ReadyRuntimeCellState)
            return new RuntimeCellIngressBaselineNotReady(runtimeCellId);

        var result = await ensureNamespace(cell, cancellationToken);
        return result switch
        {
            Unit => new RuntimeCellIngressBaselineReady(),
            RuntimeCellNamespaceOwnershipConflict => new RuntimeCellIngressBaselineNamespaceOwnershipConflict(
                runtimeCellId),
            RuntimeCellNamespaceUnavailable => new RuntimeCellIngressBaselineNamespaceUnavailable(runtimeCellId),
            RuntimeCellDeploymentTargetUnavailable targetUnavailable => targetUnavailable.Reason switch
            {
                DeploymentTargetUnavailableReason.NotFound => new RuntimeCellIngressBaselineTargetNotFound(
                    runtimeCellId),
                DeploymentTargetUnavailableReason.Disabled => new RuntimeCellIngressBaselineTargetDisabled(
                    runtimeCellId),
                DeploymentTargetUnavailableReason.DefaultNotConfigured =>
                    new RuntimeCellIngressBaselineDefaultTargetNotConfigured(runtimeCellId),
                DeploymentTargetUnavailableReason.ConnectionUnavailable =>
                    new RuntimeCellIngressBaselineTargetConnectionUnavailable(runtimeCellId),
                _ => throw new ArgumentOutOfRangeException(nameof(targetUnavailable)),
            },
            RuntimeCellNetworkPolicyOwnershipConflict => new RuntimeCellIngressBaselinePolicyOwnershipConflict(
                runtimeCellId),
            RuntimeCellNetworkPolicyUnavailable => new RuntimeCellIngressBaselinePolicyUnavailable(runtimeCellId),
        };
    }
}
