using MongoDB.Driver;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Persistence.Reads;

internal sealed class MongoWebServiceExists(IMongoCollection<WebService> webServices)
{
    public Task<bool> InvokeAsync(
        string tenantId,
        string webServiceId,
        CancellationToken cancellationToken = default) =>
        webServices.Find(WebServiceFilters.ById(tenantId, webServiceId))
            .AnyAsync(cancellationToken);
}
