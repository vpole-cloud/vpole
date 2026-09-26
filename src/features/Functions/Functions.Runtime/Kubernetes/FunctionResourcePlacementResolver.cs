using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Domain;
using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.Functions.Runtime.Kubernetes;

public sealed record FunctionResourcePlacementUnavailable(string FunctionAppId);

public sealed record FunctionResourcePlacement(
    string ResourceDeploymentId,
    string RuntimeCellId,
    string DeploymentTargetId,
    string KubernetesNamespace,
    string IsolationScopeId);

public union FunctionResourcePlacementOutcome(
    FunctionResourcePlacement,
    FunctionResourcePlacementUnavailable);

public interface IFunctionResourcePlacementResolver
{
    Task<FunctionResourcePlacementOutcome> ResolveAsync(
        FunctionApp app,
        bool requireReady = true,
        CancellationToken cancellationToken = default);
}

public sealed class FunctionResourcePlacementResolver(
    LoadExactResourceDeploymentRuntimePlacement loadPlacement)
    : IFunctionResourcePlacementResolver
{
    public async Task<FunctionResourcePlacementOutcome>
        ResolveAsync(
            FunctionApp app,
            bool requireReady = true,
            CancellationToken cancellationToken = default)
    {
        var placement = await loadPlacement(
            app.TenantId,
            FunctionResourceDeployment.ResourceType,
            app.Id,
            app.Placement.ResourceDeploymentId,
            cancellationToken);
        if (placement is null ||
            !string.Equals(
                placement.RuntimeCellId,
                app.Placement.RuntimeCellId,
                StringComparison.Ordinal) ||
            !string.Equals(
                placement.DeploymentTargetId,
                app.Placement.DeploymentTargetId,
                StringComparison.Ordinal) ||
            !string.Equals(placement.DesiredOperation, "Apply", StringComparison.Ordinal) ||
            requireReady && !string.Equals(placement.Status, "Applied", StringComparison.Ordinal))
        {
            return new FunctionResourcePlacementUnavailable(app.Id);
        }

        return new FunctionResourcePlacement(
            placement.DeploymentId,
            placement.RuntimeCellId,
            placement.DeploymentTargetId,
            placement.KubernetesNamespace,
            placement.IsolationScopeId);
    }
}
