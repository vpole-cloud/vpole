using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Application.Lifecycle;
using Pole.Features.WebServices.Application.Hostnames;
using Pole.Features.WebServices.Application.Declarations;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application;
using MongoDB.Driver;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Deployments;

public sealed record WebServiceDeploymentPersistenceSnapshot(
    WebService WebService,
    string ReleaseId,
    long ResourceRevision,
    string ResourceDeploymentId,
    long DeploymentGeneration,
    DateTime RequestedAtUtc);

public delegate Task<WebServiceDeploymentPersistenceSnapshot?> LoadWebServiceDeploymentSnapshot(
    string tenantId,
    string webServiceId,
    string resourceDeploymentId,
    long resourceRevision,
    long deploymentGeneration,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadWebServiceDeploymentSnapshot(
    IMongoCollection<WebService> webServices,
    IMongoCollection<WebServiceRelease> releases)
{
    public async Task<WebServiceDeploymentPersistenceSnapshot?> InvokeAsync(
        string tenantId,
        string webServiceId,
        string resourceDeploymentId,
        long resourceRevision,
        long deploymentGeneration,
        CancellationToken cancellationToken = default)
    {
        var webService = await webServices.Find(
                WebServiceFilters.ById(tenantId, webServiceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (webService is null)
            return null;

        var release = await releases.Find(
                WebServiceReleaseFilters.DeploymentSnapshot(
                    tenantId,
                    webServiceId,
                    resourceDeploymentId,
                    resourceRevision,
                    deploymentGeneration))
            .FirstOrDefaultAsync(cancellationToken);
        return release is null
            ? null
            : new WebServiceDeploymentPersistenceSnapshot(
                webService with
                {
                    Spec = release.Spec,
                    Exposure = release.Exposure,
                    ActiveRevision = release.ResourceRevision
                },
                release.Id,
                release.ResourceRevision,
                resourceDeploymentId,
                deploymentGeneration,
                release.RequestedAtUtc);
    }
}
