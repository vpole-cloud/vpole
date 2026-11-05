using MongoDB.Driver;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application.Connections;
using Pole.Features.WebServices.Application.Declarations;
using Pole.Features.WebServices.Application.Hostnames;
using Pole.Features.WebServices.Application.Lifecycle;
using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Queries;

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
