using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Projections;

internal static class SecretProjectionAuthority
{
    public static bool MatchesScope(Secret secret, SecretProjection projection) =>
        secret.ResourceGroupId == projection.ResourceGroupId &&
        secret.IsolationScopeId == projection.IsolationScopeId;

    public static bool MatchesConsumer(SecretBinding binding, SecretProjection projection) =>
        binding.ConsumerType == projection.ConsumerType &&
        binding.ConsumerId == projection.ConsumerId;

    public static bool IncludesDeployment(SecretDeploymentSelection selection, string deploymentId) => selection switch
    {
        AllSecretDeployments => true,
        SelectedSecretDeployments selected => selected.DeploymentIds.Contains(deploymentId),
        _ => throw new InvalidOperationException("Unknown Secret deployment selection.")
    };

    public static bool MatchesPlacement(ResourceDeploymentRuntimePlacementDto placement, SecretProjection projection) =>
        placement.DesiredOperation != "Delete" &&
        placement.IsolationScopeId == projection.IsolationScopeId &&
        placement.RuntimeCellId == projection.RuntimeCellId &&
        placement.DeploymentTargetId == projection.DeploymentTargetId &&
        placement.KubernetesNamespace == projection.KubernetesNamespace;
}
