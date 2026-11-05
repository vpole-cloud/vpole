using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application.Declarations;
using Pole.Features.WebServices.Application.Hostnames;
using Pole.Features.WebServices.Application.Lifecycle;
using Pole.Features.WebServices.Application.Connections;
using Pole.Features.WebServices.Application.Queries;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Domain;
using System.Text.RegularExpressions;

namespace Pole.Features.WebServices.Application.Mongo;

internal static class WebServiceFilters
{
    public static FilterDefinition<WebService> AtRevision(string tenantId, string resourceId, long revision) =>
        ById(tenantId, resourceId) & Builders<WebService>.Filter.Eq(item => item.Revision, revision);

    public static FilterDefinition<WebService> AtPlanningVersion(WebService service) =>
        AtRevision(service) & Builders<WebService>.Filter.Eq(item => item.PlanningFence, service.PlanningFence);

    public static FilterDefinition<WebService> ByTechnicalName(string tenantId, string name) =>
        Builders<WebService>.Filter.ForTenant(tenantId) & Builders<WebService>.Filter.Regex(item => item.Name,
            new MongoDB.Bson.BsonRegularExpression("^" + System.Text.RegularExpressions.Regex.Escape(name) + "$", "i"));

    public static FilterDefinition<WebService> VisibleToTenant(string tenantId) =>
        Builders<WebService>.Filter.ForTenant(tenantId);

    public static FilterDefinition<WebService> ForList(WebServiceListQuery query)
    {
        var filter = VisibleToTenant(query.TenantId);
        if (!string.IsNullOrWhiteSpace(query.ResourceGroupId))
            filter &= Builders<WebService>.Filter.Eq(
                item => item.ResourceGroupId,
                query.ResourceGroupId);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var name = Builders<WebService>.Filter.Regex(
                item => item.Name,
                new BsonRegularExpression(
                    $".*{Regex.Escape(query.Search)}.*",
                    "i"));
            filter &= ObjectId.TryParse(query.Search, out _)
                ? Builders<WebService>.Filter.Or(
                    name,
                    Builders<WebService>.Filter.Eq(item => item.Id, query.Search))
                : name;
        }

        return filter;
    }

    public static FilterDefinition<WebService> ById(string tenantId, string webServiceId) =>
        Builders<WebService>.Filter.And(
            VisibleToTenant(tenantId),
            Builders<WebService>.Filter.Eq(item => item.Id, webServiceId));

    public static FilterDefinition<WebService> ByIds(
        string tenantId,
        IReadOnlyCollection<string> webServiceIds) =>
        Builders<WebService>.Filter.And(
            VisibleToTenant(tenantId),
            Builders<WebService>.Filter.In(item => item.Id, webServiceIds));

    public static FilterDefinition<WebService> AtRevision(WebService webService) =>
        Builders<WebService>.Filter.And(
            ById(webService.TenantId, webService.Id),
            Builders<WebService>.Filter.Eq(item => item.Revision, webService.Revision));

    public static FilterDefinition<WebService> PrivateActiveRevision(
        string tenantId,
        string webServiceId,
        long activeRevision) =>
        Builders<WebService>.Filter.And(
            ById(tenantId, webServiceId),
            Builders<WebService>.Filter.Eq(item => item.ActiveRevision, activeRevision),
            Builders<WebService>.Filter.Eq(
                item => item.Exposure.Mode,
                WebServiceExposureMode.Private));

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
        long revision) =>
        Builders<WebServiceRelease>.Filter.And(
            ByWebService(tenantId, webServiceId),
            Builders<WebServiceRelease>.Filter.Eq(item => item.ResourceRevision, revision),
            Builders<WebServiceRelease>.Filter.Eq(
                item => item.Outcome,
                WebServiceReleaseOutcome.Succeeded));

    public static FilterDefinition<WebServiceRelease> PendingDeployment(
        string tenantId,
        string webServiceId,
        string resourceDeploymentId,
        long resourceRevision,
        long deploymentGeneration,
        WebServiceReleaseDeploymentOperation operation) =>
        Builders<WebServiceRelease>.Filter.And(
            ByWebService(tenantId, webServiceId),
            Builders<WebServiceRelease>.Filter.Eq(
                item => item.Outcome,
                WebServiceReleaseOutcome.Pending),
            Builders<WebServiceRelease>.Filter.ElemMatch(
                item => item.Deployments,
                entry => entry.ResourceDeploymentId == resourceDeploymentId &&
                         entry.ResourceRevision == resourceRevision &&
                         entry.DeploymentGeneration == deploymentGeneration &&
                         entry.Operation == operation &&
                         entry.Outcome == WebServiceReleaseDeploymentOutcome.Pending));

    public static FilterDefinition<WebServiceRelease> PendingForWebService(
        string tenantId, string webServiceId) =>
        Builders<WebServiceRelease>.Filter.And(
            ByWebService(tenantId, webServiceId),
            Builders<WebServiceRelease>.Filter.Eq(
                item => item.Outcome,
                WebServiceReleaseOutcome.Pending));

    public static FilterDefinition<WebServiceRelease> CompletionCas(
        string releaseId,
        WebServiceReleaseOutcome expectedOutcome) =>
        Builders<WebServiceRelease>.Filter.And(
            Builders<WebServiceRelease>.Filter.Eq(item => item.Id, releaseId),
            Builders<WebServiceRelease>.Filter.Eq(
                item => item.Outcome,
                expectedOutcome));

    public static FilterDefinition<WebServiceRelease> DeploymentSnapshot(
        string tenantId, string webServiceId, string resourceDeploymentId,
        long resourceRevision, long deploymentGeneration) =>
        Builders<WebServiceRelease>.Filter.And(
            ByWebService(tenantId, webServiceId),
            Builders<WebServiceRelease>.Filter.ElemMatch(
                item => item.Deployments,
                entry => entry.ResourceDeploymentId == resourceDeploymentId &&
                         entry.ResourceRevision == resourceRevision &&
                         entry.DeploymentGeneration == deploymentGeneration));
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
