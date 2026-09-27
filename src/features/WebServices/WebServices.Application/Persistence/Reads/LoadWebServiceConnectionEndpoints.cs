using MongoDB.Driver;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Kubernetes;
using Pole.Features.WebServices.Contracts;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Persistence.Reads;

internal sealed class MongoLoadWebServiceConnectionEndpoints(IMongoCollection<WebService> services)
{
    public async Task<LoadWebServiceConnectionEndpointsOutcome> InvokeAsync(
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
            return new WebServiceConnectionResourceMissing();
        if (resource.Lifecycle is DeletingWebServiceLifecycle)
            return new WebServiceConnectionResourceDeleting();
        return new WebServiceConnectionEndpointsFound(
            resource.Id,
            WebServiceInKubernetes.BuildServiceName(resource.Name),
            WebServiceConnectionEndpointCatalog.FromConfig(resource.Spec.Config));
    }
}
