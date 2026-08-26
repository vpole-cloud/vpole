using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Persistence;

public delegate Task<ResourceDeployment?> ClaimResourceDeployment(
    string tenantId,
    string deploymentId,
    string runtimeCellId,
    long generation,
    ResourceDeploymentOperation operation,
    CancellationToken cancellationToken = default);

internal sealed class ClaimResourceDeploymentOperation(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    ResourceDeploymentSettings settings,
    TimeProvider timeProvider)
{
    public async Task<ResourceDeployment?> InvokeAsync(
        string tenantId,
        string deploymentId,
        string runtimeCellId,
        long generation,
        ResourceDeploymentOperation operation,
        CancellationToken cancellationToken = default)
    {
        var current = await resourceDeployments
            .Find(ResourceDeploymentFilters.ForPendingReconciliation(
                tenantId,
                deploymentId,
                runtimeCellId,
                generation,
                operation))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null || current.State is not PendingResourceDeploymentState pending ||
            pending.Generation != generation || pending.Operation != operation)
        {
            return null;
        }

        var startedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var claimed = current.Start(
            generation,
            operation,
            ObjectId.GenerateNewId().ToString(),
            startedAtUtc,
            startedAtUtc.AddSeconds(settings.ClaimLeaseSeconds));
        var replacement = await resourceDeployments.ReplaceOneAsync(
            ResourceDeploymentFilters.ForPendingAtRevision(current),
            claimed,
            cancellationToken: cancellationToken);
        return replacement.ModifiedCount == 1 ? claimed : null;
    }
}
