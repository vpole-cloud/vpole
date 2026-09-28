using MongoDB.Driver;
using System.Collections.Immutable;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Kubernetes;
using Pole.Features.WebServices.Contracts;
using Pole.Features.WebServices.Domain;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Workloads;

namespace Pole.Features.WebServices.Application.Persistence.Reads;

internal sealed class MongoLoadWebServiceConnectionEndpoints(IMongoCollection<WebService> services)
    : IResourceConnectionDestinationCapability
{
    public ResourceTypeId ResourceType => WebServiceResourceDeployment.ResourceType;

    public async Task<LoadResourceConnectionEndpointsOutcome> LoadAsync(
        string tenantId,
        string resourceId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Connection endpoint lookup requires a transaction.");
        var resource = await services.Find(session, WebServiceFilters.ById(tenantId, resourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (resource is null)
            return new ResourceConnectionDestinationMissing();
        if (resource.Lifecycle is DeletingWebServiceLifecycle)
            return new ResourceConnectionDestinationDeleting();
        var serviceName = WebServiceInKubernetes.BuildServiceName(resource.Name);
        return new ResourceConnectionEndpointsFound(
            WebServiceConnectionEndpointCatalog.FromConfig(resource.Spec.Config, serviceName));
    }
}
