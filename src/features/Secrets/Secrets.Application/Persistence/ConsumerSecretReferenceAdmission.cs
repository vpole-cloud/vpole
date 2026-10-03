using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Workloads;

namespace Pole.Features.Secrets.Application.Persistence;

internal sealed class ConsumerSecretReferenceAdmission(
    IMongoCollection<Secret> secrets,
    AdmitResourceScopeReference admitScope,
    AdmitResourceDeploymentReferences admitDeployments,
    LoadResourceDeploymentConnectionPlacements loadPlacements)
{
    public async Task<AdmitConsumerSecretReferencesOutcome> InvokeAsync(
        string tenantId,
        string resourceGroupId,
        string isolationScopeId,
        ResourceTypeId consumerType,
        string consumerId,
        IReadOnlyCollection<ConsumerSecretKeyReference> previousReferences,
        IReadOnlyCollection<ConsumerSecretKeyReference> desiredReferences,
        bool requireAvailable,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (!session.IsInTransaction) throw new InvalidOperationException("Consumer Secret admission requires a transaction.");
        var ids = previousReferences.Concat(desiredReferences).Select(reference => reference.SecretId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        IReadOnlyList<ResourceDeploymentConnectionPlacement>? placements = null;
        foreach (var id in ids)
        {
            var desired = desiredReferences.Where(reference => reference.SecretId == id).Select(reference => reference.Key).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
            var current = await secrets.Find(session, SecretFilters.Identity(tenantId, id)).FirstOrDefaultAsync(ct);
            var binding = current?.Bindings.SingleOrDefault(item => item.ConsumerType == consumerType.Value && item.ConsumerId == consumerId);
            if (requireAvailable && desired.Length > 0)
            {
                if (current is null || binding is null || current.ResourceGroupId != resourceGroupId || current.IsolationScopeId != isolationScopeId)
                    return Unavailable(id, "SecretBindingUnavailable");
                if (desired.Any(key => !ConsumerSecretReferenceAuthority.IncludesKey(current, binding, key)))
                    return Unavailable(id, "SecretKeyUnavailable");
                placements ??= (await loadPlacements(tenantId, consumerType, consumerId, session, ct)).Where(item => !item.DeletionRequested).ToArray();
                if (placements.Count == 0 || placements.Any(placement => !SecretProjectionAuthority.IncludesDeployment(binding.Selection, placement.DeploymentId)) ||
                    await admitScope(tenantId, resourceGroupId, isolationScopeId, new ResourceScopeConsumerReference(consumerType, consumerId), session, ct) is ResourceScopeReferenceUnavailable ||
                    await admitDeployments(tenantId, resourceGroupId, isolationScopeId, consumerType, consumerId, true, [], session, ct) is ResourceDeploymentReferenceUnavailable)
                    return Unavailable(id, "SecretDeploymentCoverageUnavailable");
            }
            if (current is null || binding is null) continue; // Revocation must never block independent cleanup.
            var updatedBindings = current.Bindings.Select(item => item.Id == binding.Id ? item with { RequiredKeys = desired } : item).ToImmutableArray();
            // Pins affect admission only, not encrypted values or the projection revision.
            await secrets.UpdateOneAsync(session, SecretFilters.Identity(tenantId, id), Builders<Secret>.Update
                .Set(item => item.Bindings, updatedBindings).Inc(item => item.ReferenceFence, 1), cancellationToken: ct);
        }
        return new ConsumerSecretReferencesAdmitted();
    }

    private static ConsumerSecretReferenceUnavailable Unavailable(string id, string reason) => new(id, reason);
}

internal static class ConsumerSecretReferenceAuthority
{
    internal static bool IncludesKey(Secret secret, SecretBinding binding, string key) =>
        secret.KeyNames.Contains(key) && (binding.KeyMappings.Count == 0 || binding.KeyMappings.ContainsKey(key));
    internal static string ProjectedKey(SecretBinding binding, string key) => binding.KeyMappings.Count == 0 ? key : binding.KeyMappings[key];
}
