using MongoDB.Driver;
using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Releases;

public delegate Task<WebServiceRelease?> LoadSucceededWebServiceRelease(
    string tenantId,
    string webServiceId,
    long revision,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadSucceededWebServiceRelease(
    IMongoCollection<WebServiceRelease> releases
)
{
    public async Task<WebServiceRelease?> InvokeAsync(
        string tenantId,
        string webServiceId,
        long revision,
        CancellationToken cancellationToken = default) => await releases
        .Find(WebServiceReleaseFilters.SucceededRevision(tenantId, webServiceId, revision))
        .FirstOrDefaultAsync(cancellationToken);
}
