using System.Collections.Immutable;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Application.Persistence;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Workloads;

namespace Pole.Features.Secrets.Application.Projections;

internal sealed class ConsumerSecretProjectionResolver(
    LoadRegisteredResource loadResource,
    LoadExactResourceDeploymentRuntimePlacement loadPlacement,
    LoadSecretById loadSecret,
    LoadSecretProjections loadProjections,
    SecretProjectionReconciler reconcile)
{
    public async Task<EnsureConsumerSecretProjectionsOutcome> InvokeAsync(
        string tenantId,
        ResourceTypeId consumerType,
        string consumerId,
        string deploymentId,
        IReadOnlyCollection<ConsumerSecretKeyReference> references,
        CancellationToken ct)
    {
        if (references.Count == 0) return new ConsumerSecretProjectionKeys([]);
        var resource = await loadResource(tenantId, consumerType, consumerId, ct);
        var placement = await loadPlacement(tenantId, consumerType, consumerId, deploymentId, ct);
        if (resource is null || placement is null || placement.DesiredOperation == "Delete")
            return Unavailable(references.First().SecretId, "SecretConsumerUnavailable");
        var keys = ImmutableArray.CreateBuilder<ConsumerSecretProjectionKey>();
        foreach (var group in references.Distinct().GroupBy(reference => reference.SecretId, StringComparer.Ordinal))
        {
            var current = await loadSecret(tenantId, group.Key, ct);
            var binding = Binding(current, consumerType, consumerId);
            if (!Authorized(current, binding, resource.ResourceGroupId, placement, group)) return Unavailable(group.Key, "SecretBindingUnavailable");
            await reconcile.ReconcileAsync(tenantId, group.Key, ct);
            var latest = await loadSecret(tenantId, group.Key, ct);
            var latestBinding = Binding(latest, consumerType, consumerId);
            var latestPlacement = await loadPlacement(tenantId, consumerType, consumerId, deploymentId, ct);
            if (latestBinding?.Id != binding!.Id || latestPlacement is null || !Authorized(latest, latestBinding, resource.ResourceGroupId, latestPlacement, group))
                return Unavailable(group.Key, "SecretBindingUnavailable");
            var projection = (await loadProjections(tenantId, group.Key, ct)).SingleOrDefault(item => item.DeploymentId == deploymentId && item.BindingId == latestBinding.Id);
            if (projection is null || projection.State is not ReadySecretProjection || !SecretProjectionAuthority.MatchesPlacement(latestPlacement, projection) ||
                !SecretProjectionAuthority.MatchesScope(latest!, projection) || !SecretProjectionAuthority.MatchesConsumer(latestBinding, projection) ||
                projection.DesiredRevision != latest!.Revision || projection.DesiredVersion != latest.CurrentVersion || projection.AppliedVersion != latest.CurrentVersion)
                return new ConsumerSecretProjectionPending(group.Key);
            foreach (var reference in group)
                keys.Add(new(group.Key, reference.Key, projection.KubernetesName, ConsumerSecretReferenceAuthority.ProjectedKey(latestBinding, reference.Key), latest.CurrentVersion));
        }
        return new ConsumerSecretProjectionKeys(keys.ToImmutable());
    }

    private static SecretBinding? Binding(Secret? secret, ResourceTypeId type, string id) => secret?.Bindings.SingleOrDefault(item => item.ConsumerType == type.Value && item.ConsumerId == id);
    private static bool Authorized(Secret? secret, SecretBinding? binding, string groupId, ResourceDeploymentRuntimePlacementDto placement, IEnumerable<ConsumerSecretKeyReference> references) =>
        secret is not null && binding is not null && placement.DesiredOperation != "Delete" && secret.ResourceGroupId == groupId && secret.IsolationScopeId == placement.IsolationScopeId &&
        SecretProjectionAuthority.IncludesDeployment(binding.Selection, placement.DeploymentId) && references.All(reference => ConsumerSecretReferenceAuthority.IncludesKey(secret, binding, reference.Key));
    private static ConsumerSecretReferenceUnavailable Unavailable(string id, string reason) => new(id, reason);
}
