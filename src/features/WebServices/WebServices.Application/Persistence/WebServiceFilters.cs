using MongoDB.Driver;
using Pole.Database;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Persistence;

internal static class WebServiceFilters
{
    public static FilterDefinition<WebService> VisibleToTenant(string tenantId) =>
        Builders<WebService>.Filter.ForTenant(tenantId);

    public static FilterDefinition<WebService> ById(string tenantId, string webServiceId) =>
        Builders<WebService>.Filter.And(
            VisibleToTenant(tenantId),
            Builders<WebService>.Filter.Eq(item => item.Id, webServiceId));

    public static FilterDefinition<WebService> AtRevision(WebService webService) =>
        Builders<WebService>.Filter.And(
            ById(webService.TenantId, webService.Id),
            Builders<WebService>.Filter.Eq(item => item.Revision, webService.Revision));

    public static FilterDefinition<WebService> InLifecycle<TLifecycle>(
        string tenantId,
        string webServiceId)
        where TLifecycle : WebServiceLifecycle =>
        Builders<WebService>.Filter.And(
            ById(tenantId, webServiceId),
            Builders<WebService>.Filter.OfType<WebServiceLifecycle, TLifecycle>(
                item => item.Lifecycle));
}

internal static class WebServiceReleaseFilters
{
    public static FilterDefinition<WebServiceRelease> ByWebService(
        string tenantId,
        string webServiceId) =>
        Builders<WebServiceRelease>.Filter.And(
            Builders<WebServiceRelease>.Filter.ForTenant(tenantId),
            Builders<WebServiceRelease>.Filter.Eq(
                item => item.WebServiceId,
                webServiceId));

    public static FilterDefinition<WebServiceRelease> SucceededRevision(
        string tenantId,
        string webServiceId,
        int revision) =>
        Builders<WebServiceRelease>.Filter.And(
            ByWebService(tenantId, webServiceId),
            Builders<WebServiceRelease>.Filter.Eq(item => item.Revision, revision),
            Builders<WebServiceRelease>.Filter.Eq(
                item => item.Outcome,
                WebServiceReleaseOutcome.Succeeded));

    public static FilterDefinition<WebServiceRelease> PendingGeneration(
        string tenantId,
        string webServiceId,
        long deploymentGeneration) =>
        Builders<WebServiceRelease>.Filter.And(
            ByWebService(tenantId, webServiceId),
            Builders<WebServiceRelease>.Filter.Eq(
                item => item.DeploymentGeneration,
                deploymentGeneration),
            Builders<WebServiceRelease>.Filter.Eq(
                item => item.Outcome,
                WebServiceReleaseOutcome.Pending));

    public static FilterDefinition<WebServiceRelease> OlderPendingGeneration(
        WebServiceRelease release) =>
        Builders<WebServiceRelease>.Filter.And(
            ByWebService(release.TenantId, release.WebServiceId),
            Builders<WebServiceRelease>.Filter.Eq(
                item => item.Outcome,
                WebServiceReleaseOutcome.Pending),
            Builders<WebServiceRelease>.Filter.Lt(
                item => item.DeploymentGeneration,
                release.DeploymentGeneration));

    public static FilterDefinition<WebServiceRelease> ByDeploymentGeneration(
        string tenantId,
        string webServiceId,
        long deploymentGeneration) =>
        Builders<WebServiceRelease>.Filter.And(
            ByWebService(tenantId, webServiceId),
            Builders<WebServiceRelease>.Filter.Eq(
                item => item.DeploymentGeneration,
                deploymentGeneration));
}

internal static class WebServiceDeploymentTokenFilters
{
    public static FilterDefinition<WebServiceDeploymentToken> ByWebService(
        string tenantId,
        string webServiceId) =>
        Builders<WebServiceDeploymentToken>.Filter.And(
            Builders<WebServiceDeploymentToken>.Filter.ForTenant(tenantId),
            Builders<WebServiceDeploymentToken>.Filter.Eq(
                item => item.WebServiceId,
                webServiceId));

    public static FilterDefinition<WebServiceDeploymentToken> ById(
        string tenantId,
        string webServiceId,
        string tokenId) =>
        Builders<WebServiceDeploymentToken>.Filter.And(
            ByWebService(tenantId, webServiceId),
            Builders<WebServiceDeploymentToken>.Filter.Eq(item => item.Id, tokenId));

    // The unguessable token establishes tenant context; all subsequent access is tenant fenced.
    public static FilterDefinition<WebServiceDeploymentToken> ByTokenHash(string tokenHash) =>
        Builders<WebServiceDeploymentToken>.Filter.Eq(item => item.TokenHash, tokenHash);
}
