using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.Common;

public sealed record RuntimeCellDto(
    string Id,
    string ResourceGroupId,
    string IsolationScopeId,
    string DeploymentTargetId,
    string KubernetesNamespace,
    long DesiredGeneration,
    long ObservedGeneration,
    string State,
    string? FailureCode,
    DateTime? RetryNotBeforeUtc,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc
);

internal static class RuntimeCellDtoExtensions
{
    public static RuntimeCellDto ToDto(this RuntimeCell cell)
    {
        var failed = cell.State as FailedRuntimeCellState;
        return new RuntimeCellDto(
            cell.Id,
            cell.ResourceGroupId,
            cell.IsolationScopeId,
            cell.DeploymentTargetId,
            cell.KubernetesNamespace,
            cell.DesiredGeneration,
            cell.ObservedGeneration,
            cell.State switch
            {
                ProvisioningRuntimeCellState => "provisioning",
                ReadyRuntimeCellState => "ready",
                FailedRuntimeCellState => "failed",
                DeletingRuntimeCellState => "deleting",
                DeletedRuntimeCellState => "deleted",
                _ => throw new InvalidOperationException(
                    $"Unsupported runtime cell state '{cell.State.GetType().Name}'."),
            },
            failed?.FailureCode,
            failed?.RetryNotBeforeUtc,
            cell.CreatedAtUtc,
            cell.UpdatedAtUtc);
    }
}
