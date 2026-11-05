using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Mongo;
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
            new LoadExactResourceDeploymentRuntimePlacementRequest(
                TenantId: app.TenantId,
                ResourceType: FunctionResourceDeployment.ResourceType,
                ResourceId: app.Id,
                DeploymentId: app.Placement.ResourceDeploymentId),
            cancellationToken: cancellationToken);
        if (placement is null ||
            !string.Equals(
                placement.RuntimeCellId,
                app.Placement.RuntimeCellId,
                StringComparison.Ordinal) ||
            !string.Equals(
                placement.DeploymentTargetId,
                app.Placement.DeploymentTargetId,
                StringComparison.Ordinal) ||
            placement.DesiredOperation != ResourceDeploymentDesiredOperation.Apply ||
            requireReady && placement.Status != ResourceDeploymentStatus.Applied)
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
