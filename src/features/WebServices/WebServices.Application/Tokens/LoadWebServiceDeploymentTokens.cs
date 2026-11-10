using MongoDB.Driver;
using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Tokens;

public delegate Task<IReadOnlyList<WebServiceDeploymentToken>> LoadWebServiceDeploymentTokens(
    string tenantId,
    string webServiceId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadWebServiceDeploymentTokens(
    IMongoCollection<WebServiceDeploymentToken> tokens
)
{
    public async Task<IReadOnlyList<WebServiceDeploymentToken>> InvokeAsync(
        string tenantId,
        string webServiceId,
        CancellationToken cancellationToken = default) => await tokens
        .Find(WebServiceDeploymentTokenFilters.ByWebService(tenantId, webServiceId))
        .SortByDescending(item => item.CreatedAtUtc)
        .ToListAsync(cancellationToken);
}
