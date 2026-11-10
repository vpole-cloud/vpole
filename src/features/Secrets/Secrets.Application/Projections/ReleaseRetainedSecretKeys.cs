using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Workloads;

namespace Pole.Features.Secrets.Application.Projections;

public delegate Task<bool> ReleaseRetainedSecretKeys(
    string tenantId,
    string secretId,
    long revision,
    long referenceFence,
    string bindingId,
    IReadOnlyList<SecretConsumerDeploymentProof> proofs,
    CancellationToken ct);

internal sealed class MongoReleaseRetainedSecretKeys(
    IMongoCollection<Secret> secrets,
    MongoRunTransaction transactions,
    LoadResourceDeploymentConnectionPlacements loadPlacements,
    LoadExactResourceDeploymentAccessPlacement loadAndFence
)
{
    public Task<bool> InvokeAsync(
        string tenantId,
        string secretId,
        long revision,
        long referenceFence,
        string bindingId,
        IReadOnlyList<SecretConsumerDeploymentProof> proofs,
        CancellationToken ct) => transactions.InvokeOutcomeAsync<bool>(
        async (tx, token) =>
        {
            var current = await secrets.Find(tx.Session, SecretFilters.Identity(tenantId, secretId))
                .FirstOrDefaultAsync(token);
            if (current is null || current.Revision != revision || current.ReferenceFence != referenceFence)
                return tx.Abort(false);
            var binding = current.Bindings.SingleOrDefault(item => item.Id == bindingId);
            if (binding is null || binding.Pins.Retained.IsEmpty)
                return tx.Abort(false);
            var type = ResourceTypeId.Create(binding.Consumer.ResourceType);
            var placements = await loadPlacements(
                new LoadResourceDeploymentConnectionPlacementsRequest(
                    TenantId: tenantId,
                    ResourceType: type,
                    ResourceId: binding.Consumer.ResourceId),
                session: tx.Session,
                cancellationToken: token);
            if (placements.Count == 0 || placements.Any(item => item.DeletionRequested) ||
                proofs.Count != placements.Count ||
                proofs.Select(item => item.DeploymentId).Distinct().Count() != proofs.Count || !placements
                    .Select(item => item.DeploymentId)
                    .ToHashSet(StringComparer.Ordinal)
                    .SetEquals(proofs.Select(item => item.DeploymentId)))
                return tx.Abort(false);
            foreach (var proof in proofs)
            {
                var placement = await loadAndFence(
                    new LoadExactResourceDeploymentAccessPlacementRequest(
                        TenantId: tenantId,
                        ResourceType: type,
                        ResourceId: binding.Consumer.ResourceId,
                        DeploymentId: proof.DeploymentId),
                    session: tx.Session,
                    cancellationToken: token);
                if (placement is null || placement.ResourceGroupId != current.ResourceGroupId ||
                    placement.IsolationScopeId != current.IsolationScopeId ||
                    placement.DeploymentRevision != proof.DeploymentRevision ||
                    placement.DesiredGeneration != proof.DesiredGeneration ||
                    placement.Status != ResourceDeploymentStatus.Applied ||
                    placement.DesiredOperation != ResourceDeploymentDesiredOperation.Apply)
                    return tx.Abort(false);
            }

            // Admission-only state: do not publish a projection revision or a value event.
            await secrets.ReplaceOneAsync(
                tx.Session,
                SecretFilters.Identity(tenantId, secretId),
                current.ReleaseRetainedKeys(bindingId),
                cancellationToken: token);
            return tx.Commit(true);
        },
        ct);
}
