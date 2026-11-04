using MongoDB.Driver;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Persistence;

internal sealed class ConsumerSecretPlanningSnapshots(
    IMongoCollection<Secret> secrets,
    SecretPlanningFence fence)
{
    public async Task<LoadConsumerSecretPlanningSnapshotOutcome> ReadAsync(ConsumerSecretPlanningRequest request,
        IClientSessionHandle session, CancellationToken ct)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Secret planning snapshots require a transaction.");

        var versions = new List<SecretPlanningVersion>();
        var ids = request.PreviousReferences.Concat(request.DesiredReferences).Select(item => item.SecretId)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var desired = request.DesiredReferences.Where(item => item.SecretId == id).Select(item => item.Key)
                .Distinct(StringComparer.Ordinal).ToArray();
            var current = await secrets.Find(session, SecretFilters.Identity(request.TenantId, id))
                .FirstOrDefaultAsync(ct);
            var binding = current?.Bindings.SingleOrDefault(item =>
                item.Consumer.ResourceType == request.ConsumerType.Value &&
                item.Consumer.ResourceId == request.ConsumerId);
            if (desired.Length > 0)
            {
                if (current is null || binding is null || current.ResourceGroupId != request.ResourceGroupId ||
                    current.IsolationScopeId != request.IsolationScopeId)
                    return new ConsumerSecretReferenceUnavailable(id,
                        ConsumerSecretReferenceError.SecretBindingUnavailable);
                if (desired.Any(key => !current.HasValue(key)))
                    return new ConsumerSecretReferenceUnavailable(id, ConsumerSecretReferenceError.SecretValuesMissing);
                if (desired.Any(key => !ConsumerSecretReferenceAuthority.IncludesKey(current, binding, key)))
                    return new ConsumerSecretReferenceUnavailable(id,
                        ConsumerSecretReferenceError.SecretKeyUnavailable);
                if (request.DeploymentIds.Count == 0 || request.DeploymentIds.Any(deployment =>
                        !SecretProjectionAuthority.IncludesDeployment(binding.Selection, deployment)))
                    return new ConsumerSecretReferenceUnavailable(id,
                        ConsumerSecretReferenceError.SecretDeploymentCoverageUnavailable);
            }

            if (current is not null)
                versions.Add(new(current.Id, current.Revision, current.ReferenceFence));
        }

        return new ConsumerSecretPlanningSnapshot(request, versions);
    }

    public async Task<FenceConsumerSecretPlanningSnapshotOutcome> FenceAsync(ConsumerSecretPlanningSnapshot request,
        IClientSessionHandle session, CancellationToken ct)
    {
        var read = await ReadAsync(request.Request, session, ct);
        if (read is ConsumerSecretReferenceUnavailable unavailable)
            return unavailable;

        var current = (ConsumerSecretPlanningSnapshot)read.Value;
        if (!current.Secrets.SequenceEqual(request.Secrets))
            return new ConsumerSecretPlanningChanged();

        foreach (var expected in current.Secrets)
        {
            var secret = await secrets.Find(session,
                    SecretFilters.Identity(request.Request.TenantId, expected.SecretId))
                .SingleAsync(ct);
            if (!await fence.FenceAsync(secret, session, ct))
                return new ConsumerSecretPlanningChanged();
        }

        return new ConsumerSecretPlanningFenced();
    }
}
