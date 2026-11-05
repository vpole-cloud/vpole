using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Lifecycle;
using Pole.Features.Secrets.Application.Declarations;
using Pole.Features.Secrets.Application.Bootstrap;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application;
using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Workloads;

namespace Pole.Features.Secrets.Application.References;

internal sealed class ConsumerSecretReferenceAdmission(
    IMongoCollection<Secret> secrets,
    AdmitResourceScopeReference admitScope,
    AdmitResourceDeploymentReferences admitDeployments,
    LoadResourceDeploymentConnectionPlacements loadPlacements)
{
    public async Task<AdmitConsumerSecretReferencesOutcome> InvokeAsync(
        AdmitConsumerSecretReferencesRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (!session.IsInTransaction) throw new InvalidOperationException("Consumer Secret admission requires a transaction.");
        var requireAvailable = request.Availability switch
        {
            ConsumerSecretAvailabilityPolicy.RequireAvailable => true,
            ConsumerSecretAvailabilityPolicy.AllowUnavailableForCleanup => false,
            _ => throw new ArgumentOutOfRangeException(nameof(request.Availability))
        };
        var ids =
            request.PreviousReferences.Concat(request.DesiredReferences).Select(reference =>
            reference.SecretId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        IReadOnlyList<ResourceDeploymentConnectionPlacement>? placements = null;
        foreach (var id in ids)
        {
            var desired =
                request.DesiredReferences.Where(reference => reference.SecretId == id).Select(reference =>
                reference.Key).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
            var current = await secrets.Find(session, SecretFilters.Identity(request.TenantId,
                id)).FirstOrDefaultAsync(ct);
            var binding = current?.Bindings.SingleOrDefault(item =>
                item.Usage == Pole.Features.Secrets.Primitives.SecretBindingUsage.Runtime &&
                item.Consumer.ResourceType == request.ConsumerType.Value &&
                item.Consumer.ResourceId == request.ConsumerId);
            if (requireAvailable && desired.Length > 0)
            {
                if (current is null || binding is null || current.ResourceGroupId != request.ResourceGroupId
                    || current.IsolationScopeId != request.IsolationScopeId)
                    return Unavailable(id, ConsumerSecretReferenceError.SecretBindingUnavailable);
                if (desired.Any(key => !current.HasValue(key)))
                    return Unavailable(id, ConsumerSecretReferenceError.SecretValuesMissing);
                if (desired.Any(key => !ConsumerSecretReferenceAuthority.IncludesKey(current, binding, key)))
                    return Unavailable(id, ConsumerSecretReferenceError.SecretKeyUnavailable);
                placements ??= (await loadPlacements(
                    new LoadResourceDeploymentConnectionPlacementsRequest(
                        TenantId: request.TenantId,
                        ResourceType: request.ConsumerType,
                        ResourceId: request.ConsumerId),
                    session: session,
                    cancellationToken: ct)).Where(item => !item.DeletionRequested).ToArray();
                if (placements.Count == 0 || placements.Any(placement => !SecretProjectionAuthority.IncludesDeployment(binding.Selection, placement.DeploymentId)) ||
                    await admitScope(
                        new AdmitResourceScopeReferenceRequest(
                            TenantId: request.TenantId,
                            ResourceGroupId: request.ResourceGroupId,
                            IsolationScopeId: request.IsolationScopeId,
                            Consumer: new ResourceScopeConsumerReference(request.ConsumerType, request.ConsumerId)),
                        session: session,
                        cancellationToken: ct) is ResourceScopeReferenceUnavailable ||
                    await admitDeployments(
                        new AdmitResourceDeploymentReferencesRequest(
                            TenantId: request.TenantId,
                            ResourceGroupId: request.ResourceGroupId,
                            IsolationScopeId: request.IsolationScopeId,
                            ConsumerType: request.ConsumerType,
                            ConsumerId: request.ConsumerId,
                            AllDeployments: true,
                            DeploymentIds: []),
                        session: session,
                        cancellationToken: ct) is ResourceDeploymentReferenceUnavailable)
                    return Unavailable(id, ConsumerSecretReferenceError.SecretDeploymentCoverageUnavailable);
            }
            if (current is null || binding is null) continue; // Revocation must never block independent cleanup.
            var pinned = request.Retention switch
            {
                PreviousSecretReferenceRetention.DoNotRetain => binding.RequireKeys(desired),
                PreviousSecretReferenceRetention.KeepUntilRolloutConfirmed
                    => binding.RequireKeysThroughRollout(desired),
                _ => throw new ArgumentOutOfRangeException(nameof(request.Retention))
            };
            var updatedBindings = current.Bindings.Select(item => item.Id == binding.Id ? pinned
                : item).ToImmutableArray();
            // Pins affect admission only, not encrypted values or the projection revision.
            await secrets.UpdateOneAsync(session, SecretFilters.Identity(request.TenantId, id), Builders<Secret>.Update
                .Set(item => item.Bindings, updatedBindings).Inc(item => item.ReferenceFence, 1), cancellationToken: ct);
        }
        return new ConsumerSecretReferencesAdmitted();
    }

    private static ConsumerSecretReferenceUnavailable Unavailable(string id,
        ConsumerSecretReferenceError reason) => new(id, reason);
}

internal static class ConsumerSecretReferenceAuthority
{
    internal static bool IncludesKey(Secret secret, SecretBinding binding, string key) =>
        secret.KeyNames.Contains(key) && (binding.KeyMappings.Count == 0 || binding.KeyMappings.ContainsKey(key));
}
