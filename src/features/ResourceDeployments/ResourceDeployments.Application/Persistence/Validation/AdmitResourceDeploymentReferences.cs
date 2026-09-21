using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.ResultType;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Validation;

internal sealed class MongoAdmitResourceDeploymentReferences(IMongoCollection<ResourceDeployment> deployments)
{
    public async Task<Result<Unit, ResourceDeploymentReferenceUnavailable>> InvokeAsync(
        string tenantId,
        string resourceGroupId,
        string isolationScopeId,
        ResourceTypeId consumerType,
        string consumerId,
        bool allDeployments,
        IReadOnlyCollection<string> deploymentIds,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Reference admission requires a transaction.");

        var filter = ResourceDeploymentFilters.ForResource(tenantId, consumerType, consumerId);
        var current = await deployments.Find(session, filter).ToListAsync(cancellationToken);
        var active = current.Where(x => x.DesiredOperation != ResourceDeploymentOperation.Delete).ToArray();
        // All-deployment bindings are dynamic, but creation requires a live consumer in this scope.
        if (active.Length == 0 || active.Any(x => x.ResourceGroupId != resourceGroupId || x.IsolationScopeId != isolationScopeId))
            return new Err<ResourceDeploymentReferenceUnavailable>(new());

        var selected = allDeployments ? active : active.Where(x => deploymentIds.Contains(x.Id)).ToArray();
        if (!allDeployments && (selected.Length != deploymentIds.Count || selected.Length == 0))
            return new Err<ResourceDeploymentReferenceUnavailable>(new());
        foreach (var deployment in selected)
            await deployments.UpdateOneAsync(
                session,
                ResourceDeploymentFilters.ForExactDeployment(tenantId, consumerType, consumerId, deployment.Id),
                Builders<ResourceDeployment>.Update.Inc(x => x.ReferenceFence, 1),
                cancellationToken: cancellationToken);
        return new Ok<Unit>(Unit.Value);
    }
}
