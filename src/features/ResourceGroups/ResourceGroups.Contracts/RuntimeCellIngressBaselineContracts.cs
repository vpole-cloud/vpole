namespace Pole.Features.ResourceGroups.Contracts;

public sealed record RuntimeCellIngressBaselineReady;

public sealed record RuntimeCellIngressBaselineNotReady(string RuntimeCellId);

public sealed record RuntimeCellIngressBaselinePlacementMismatch(string RuntimeCellId);

public enum RuntimeCellIngressBaselineFailureReason
{
    NamespaceOwnershipConflict,
    NamespaceUnavailable,
    PolicyOwnershipConflict,
    PolicyUnavailable
}

public sealed record RuntimeCellIngressBaselineUnavailable(
    string RuntimeCellId,
    RuntimeCellIngressBaselineFailureReason Reason);

public union EnsureRuntimeCellIngressBaselineOutcome(
    RuntimeCellIngressBaselineReady,
    RuntimeCellIngressBaselineNotReady,
    RuntimeCellIngressBaselinePlacementMismatch,
    RuntimeCellIngressBaselineUnavailable);

public delegate Task<EnsureRuntimeCellIngressBaselineOutcome> EnsureRuntimeCellIngressBaseline(
    string tenantId,
    string runtimeCellId,
    string deploymentTargetId,
    string kubernetesNamespace,
    CancellationToken cancellationToken = default);
