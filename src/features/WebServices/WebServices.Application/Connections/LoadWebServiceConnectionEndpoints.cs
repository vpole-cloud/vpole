using Pole.Features.WebServices.Application.Queries;
using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Application.Lifecycle;
using Pole.Features.WebServices.Application.Hostnames;
using Pole.Features.WebServices.Application.Declarations;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application;
using MongoDB.Driver;
using System.Collections.Immutable;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Kubernetes;
using Pole.Features.WebServices.Contracts;
using Pole.Features.WebServices.Domain;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Workloads;

namespace Pole.Features.WebServices.Application.Connections;

internal sealed class MongoLoadWebServiceConnectionEndpoints(IMongoCollection<WebService> services)
    : IResourceConnectionDestinationCapability
{
    public ResourceTypeId ResourceType => WebServiceResourceDeployment.ResourceType;

    public async Task<LoadResourceConnectionEndpointsOutcome> LoadAsync(
        LoadResourceConnectionDestinationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Connection endpoint lookup requires a transaction.");
        var resource = await services.Find(session, WebServiceFilters.ById(request.TenantId, request.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (resource is null)
            return new ResourceConnectionDestinationMissing();
        if (resource.Lifecycle is DeletingWebServiceLifecycle)
            return new ResourceConnectionDestinationDeleting();
        return Endpoints(resource);
    }

    public async Task<IReadOnlyDictionary<string, ResourceConnectionEndpointsFound>> LoadAvailableAsync(
        string tenantId,
        IReadOnlyCollection<string> resourceIds,
        CancellationToken cancellationToken = default)
    {
        if (resourceIds.Count == 0)
            return new Dictionary<string, ResourceConnectionEndpointsFound>();
        var resources = await services.Find(WebServiceFilters.ByIds(tenantId, resourceIds))
            .ToListAsync(cancellationToken);
        return resources
            .Where(resource => resource.Lifecycle is not DeletingWebServiceLifecycle)
            .ToDictionary(resource => resource.Id, Endpoints, StringComparer.Ordinal);
    }

    private static ResourceConnectionEndpointsFound Endpoints(WebService resource)
    {
        var serviceName = WebServiceInKubernetes.BuildServiceName(resource.Name);
        return new ResourceConnectionEndpointsFound(
            WebServiceConnectionEndpointCatalog.FromConfig(resource.Spec.Config, serviceName));
    }
}
