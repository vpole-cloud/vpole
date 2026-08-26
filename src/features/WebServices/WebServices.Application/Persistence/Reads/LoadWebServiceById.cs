using MongoDB.Driver;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Persistence.Reads;

public delegate Task<WebService?> LoadWebServiceById(
    string tenantId,
    string webServiceId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadWebServiceById(IMongoCollection<WebService> webServices)
{
    public async Task<WebService?> InvokeAsync(
        string tenantId,
        string webServiceId,
        CancellationToken cancellationToken = default) =>
        await webServices.Find(WebServiceFilters.ById(tenantId, webServiceId))
            .FirstOrDefaultAsync(cancellationToken);
}
