using MongoDB.Driver;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Persistence;

public delegate Task<WebServiceRelease?> LoadSucceededWebServiceRelease(
    string tenantId,
    string webServiceId,
    int revision,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadSucceededWebServiceRelease(
    IMongoCollection<WebServiceRelease> releases)
{
    public async Task<WebServiceRelease?> InvokeAsync(
        string tenantId,
        string webServiceId,
        int revision,
        CancellationToken cancellationToken = default) =>
        await releases.Find(WebServiceReleaseFilters.SucceededRevision(
                tenantId,
                webServiceId,
                revision))
            .FirstOrDefaultAsync(cancellationToken);
}
