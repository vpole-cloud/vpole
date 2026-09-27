using Pole.Features.ResourceGroups.Application.Persistence.RuntimeCells;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Kubernetes;

internal sealed class KubernetesEnsureRuntimeCellIngressBaseline(
    RuntimeCellLifecyclePersistence persistence,
    EnsureRuntimeCellNamespace ensureNamespace)
{
    public async Task<EnsureRuntimeCellIngressBaselineOutcome> InvokeAsync(
        string tenantId,
        string runtimeCellId,
        string deploymentTargetId,
        string kubernetesNamespace,
        CancellationToken cancellationToken = default)
    {
        var cell = await persistence.LoadAsync(tenantId, runtimeCellId, cancellationToken);
        if (cell is null ||
            cell.DeploymentTargetId != deploymentTargetId ||
            cell.KubernetesNamespace != kubernetesNamespace)
        {
            return new RuntimeCellIngressBaselinePlacementMismatch(runtimeCellId);
        }

        if (cell.State is not ReadyRuntimeCellState)
            return new RuntimeCellIngressBaselineNotReady(runtimeCellId);

        var result = await ensureNamespace(cell, cancellationToken);
        return result switch
        {
            Unit => new RuntimeCellIngressBaselineReady(),
            RuntimeCellNamespaceOwnershipConflict => new RuntimeCellIngressBaselineUnavailable(
                runtimeCellId,
                RuntimeCellIngressBaselineFailureReason.NamespaceOwnershipConflict),
            RuntimeCellNamespaceUnavailable => new RuntimeCellIngressBaselineUnavailable(
                runtimeCellId,
                RuntimeCellIngressBaselineFailureReason.NamespaceUnavailable),
            RuntimeCellDeploymentTargetUnavailable targetUnavailable =>
                new RuntimeCellIngressBaselineTargetUnavailable(
                    runtimeCellId,
                    targetUnavailable.Reason switch
                    {
                        DeploymentTargetUnavailableReason.NotFound =>
                            RuntimeCellIngressBaselineTargetFailureReason.NotFound,
                        DeploymentTargetUnavailableReason.Disabled =>
                            RuntimeCellIngressBaselineTargetFailureReason.Disabled,
                        DeploymentTargetUnavailableReason.DefaultNotConfigured =>
                            RuntimeCellIngressBaselineTargetFailureReason.DefaultNotConfigured,
                        DeploymentTargetUnavailableReason.ConnectionUnavailable =>
                            RuntimeCellIngressBaselineTargetFailureReason.ConnectionUnavailable,
                        _ => throw new ArgumentOutOfRangeException(nameof(targetUnavailable))
                    }),
            RuntimeCellNetworkPolicyOwnershipConflict => new RuntimeCellIngressBaselineUnavailable(
                runtimeCellId,
                RuntimeCellIngressBaselineFailureReason.PolicyOwnershipConflict),
            RuntimeCellNetworkPolicyUnavailable => new RuntimeCellIngressBaselineUnavailable(
                runtimeCellId,
                RuntimeCellIngressBaselineFailureReason.PolicyUnavailable)
        };
    }
}
