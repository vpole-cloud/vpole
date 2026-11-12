using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Application.References;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Primitives;
using Pole.Workloads;

namespace Pole.Features.Secrets.Application.Projections;

internal sealed class ConsumerSecretProjectionResolver(
    LoadRegisteredResource loadResource,
    LoadExactResourceDeploymentRuntimePlacement loadPlacement,
    LoadSecretById loadSecret,
    LoadSecretProjections loadProjections,
    SecretProjectionReconciler reconcile
)
{
    public async Task<EnsureConsumerSecretProjectionsOutcome> InvokeAsync(
        EnsureConsumerSecretProjectionsRequest request,
        CancellationToken ct)
    {
        if (request.References.Count == 0)
            return new ConsumerSecretProjectionKeys([]);

        var resource = await loadResource(
            new LoadRegisteredResourceRequest(
                TenantId: request.TenantId,
                ResourceTypeId: request.ConsumerType,
                ResourceId: request.ConsumerId),
            cancellationToken: ct);
        var placement = await loadPlacement(
            new LoadExactResourceDeploymentRuntimePlacementRequest(
                TenantId: request.TenantId,
                ResourceType: request.ConsumerType,
                ResourceId: request.ConsumerId,
                DeploymentId: request.DeploymentId),
            cancellationToken: ct);
        if (resource is null || placement is null ||
            placement.DesiredOperation == ResourceDeploymentDesiredOperation.Delete)
            return new ConsumerSecretReferenceUnavailable(
                request.References.First().SecretId,
                ConsumerSecretReferenceError.SecretConsumerUnavailable);

        var keys = ImmutableArray.CreateBuilder<ConsumerSecretProjectionKey>();
        foreach (var secretReferences in request.References
                     .Distinct()
                     .GroupBy(reference => reference.SecretId, StringComparer.Ordinal))
        {
            var secretId = secretReferences.Key;
            var current = await loadSecret(request.TenantId, secretId, ct);
            var binding = FindBinding(current, request.ConsumerType, request.ConsumerId);
            if (!IsAuthorized(current, binding, resource.ResourceGroupId, placement, secretReferences))
                return new ConsumerSecretReferenceUnavailable(
                    secretId,
                    ConsumerSecretReferenceError.SecretBindingUnavailable);

            await reconcile.ReconcileAsync(request.TenantId, secretId, ct);

            var latest = await loadSecret(request.TenantId, secretId, ct);
            var latestBinding = FindBinding(latest, request.ConsumerType, request.ConsumerId);
            var latestPlacement = await loadPlacement(
                new LoadExactResourceDeploymentRuntimePlacementRequest(
                    TenantId: request.TenantId,
                    ResourceType: request.ConsumerType,
                    ResourceId: request.ConsumerId,
                    DeploymentId: request.DeploymentId),
                cancellationToken: ct);
            if (latestPlacement is null || !IsAuthorized(
                    latest,
                    latestBinding,
                    resource.ResourceGroupId,
                    latestPlacement,
                    secretReferences) || latestBinding.Id != binding.Id)
                return new ConsumerSecretReferenceUnavailable(
                    secretId,
                    ConsumerSecretReferenceError.SecretBindingUnavailable);

            var projections = await loadProjections(request.TenantId, secretId, ct);
            var projection = projections.SingleOrDefault(item =>
                item.DeploymentId == request.DeploymentId && item.BindingId == latestBinding.Id);
            if (projection is null || projection.State is not ReadySecretProjection ||
                !SecretProjectionAuthority.MatchesPlacement(latestPlacement, projection) ||
                !SecretProjectionAuthority.MatchesScope(latest, projection) ||
                !SecretProjectionAuthority.MatchesConsumer(latestBinding, projection) ||
                projection.DesiredRevision != latest.Revision || projection.DesiredVersion != latest.CurrentVersion ||
                projection.AppliedVersion != latest.CurrentVersion)
                return new ConsumerSecretProjectionPending(secretId);

            foreach (var reference in secretReferences)
                keys.Add(
                    new ConsumerSecretProjectionKey(
                        secretId,
                        reference.Key,
                        projection.KubernetesName,
                        latestBinding.ProjectedKey(reference.Key),
                        latest.CurrentVersion));
        }

        return new ConsumerSecretProjectionKeys(keys.ToImmutable());
    }

    private static SecretBinding? FindBinding(
        Secret? secret,
        ResourceTypeId type,
        string id) => secret?.Bindings.SingleOrDefault(binding =>
        binding.GrantsRuntimeProjection() && binding.Consumer.ResourceType == type.Value &&
        binding.Consumer.ResourceId == id);

    private static bool IsAuthorized(
        [NotNullWhen(true)] Secret? secret,
        [NotNullWhen(true)] SecretBinding? binding,
        string groupId,
        ResourceDeploymentRuntimePlacementDto placement,
        IEnumerable<ConsumerSecretKeyReference> references) => secret is not null && binding is not null &&
        placement.DesiredOperation != ResourceDeploymentDesiredOperation.Delete && secret.ResourceGroupId == groupId &&
        secret.IsolationScopeId == placement.IsolationScopeId &&
        SecretProjectionAuthority.IncludesDeployment(binding.Selection, placement.DeploymentId) &&
        references.All(reference => secret.HasValue(reference.Key) &&
            ConsumerSecretReferenceAuthority.IncludesKey(secret, binding, reference.Key));
}
