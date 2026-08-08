using MongoDB.Driver;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Persistence;

public sealed record WebServiceDeploymentPersistenceSnapshot(
    WebService WebService,
    string ReleaseId,
    int Revision,
    DateTime RequestedAtUtc);

public delegate Task<WebServiceDeploymentPersistenceSnapshot?> LoadWebServiceDeploymentSnapshot(
    string tenantId,
    string webServiceId,
    long deploymentGeneration,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadWebServiceDeploymentSnapshot(
    IMongoCollection<WebService> webServices,
    IMongoCollection<WebServiceRelease> releases)
{
    public async Task<WebServiceDeploymentPersistenceSnapshot?> InvokeAsync(
        string tenantId,
        string webServiceId,
        long deploymentGeneration,
        CancellationToken cancellationToken = default)
    {
        var webService = await webServices.Find(
                WebServiceFilters.ById(tenantId, webServiceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (webService is null)
            return null;

        var release = await releases.Find(
                WebServiceReleaseFilters.ByDeploymentGeneration(
                    tenantId,
                    webServiceId,
                    deploymentGeneration))
            .FirstOrDefaultAsync(cancellationToken);
        return release is null
            ? null
            : new WebServiceDeploymentPersistenceSnapshot(
                webService with
                {
                    Spec = release.Spec.ToDomain(),
                    ActiveRevision = release.Revision
                },
                release.Id,
                release.Revision,
                release.RequestedAtUtc);
    }
}
