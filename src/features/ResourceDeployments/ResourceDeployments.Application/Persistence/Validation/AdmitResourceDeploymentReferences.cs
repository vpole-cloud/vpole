using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Validation;

internal sealed class MongoAdmitResourceDeploymentReferences(IMongoCollection<ResourceDeployment> deployments)
{
    public async Task<AdmitResourceDeploymentReferencesOutcome> InvokeAsync(
        AdmitResourceDeploymentReferencesRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Reference admission requires a transaction.");

        var filter = ResourceDeploymentFilters.ForResource(request.TenantId, request.ConsumerType, request.ConsumerId);
        var current = await deployments.Find(session, filter).ToListAsync(cancellationToken);
        var active = current.Where(x => x.DesiredOperation != ResourceDeploymentOperation.Delete).ToArray();
        // All-deployment bindings are dynamic, but creation requires a live consumer in this scope.
        if (active.Length == 0 || active.Any(x => x.ResourceGroupId != request.ResourceGroupId || x.IsolationScopeId != request.IsolationScopeId))
            return new ResourceDeploymentReferenceUnavailable();

        var selected = request.AllDeployments ? active : active.Where(x => request.DeploymentIds.Contains(x.Id)).ToArray();
        if (!request.AllDeployments && (selected.Length != request.DeploymentIds.Count || selected.Length == 0))
            return new ResourceDeploymentReferenceUnavailable();
        foreach (var deployment in selected)
            await deployments.UpdateOneAsync(
                session,
                ResourceDeploymentFilters.ForExactDeployment(request.TenantId, request.ConsumerType, request.ConsumerId, deployment.Id),
                Builders<ResourceDeployment>.Update.Inc(x => x.ReferenceFence, 1),
                cancellationToken: cancellationToken);
        return new ResourceDeploymentReferencesAdmitted();
    }
}
