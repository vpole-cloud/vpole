using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Domains.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Persistence;

public delegate Task<Result> FinalizeWebServiceDeletion(
    string tenantId,
    string webServiceId,
    CancellationToken cancellationToken = default);

internal sealed class MongoFinalizeWebServiceDeletion(
    IMongoCollection<WebService> webServices,
    GetResourceDeploymentFinalizationReadiness getResourceDeploymentFinalizationReadiness,
    PurgeDeletedResourceDeploymentsLegacy purgeDeletedResourceDeployments,
    DeleteHostnameBindingsForResource deleteHostnameBindingsForResource,
    RunMongoTransaction runMongoTransaction,
    StoreOutboxWrites storeOutboxWrites)
{
    public Task<Result> InvokeAsync(
        string tenantId,
        string webServiceId,
        CancellationToken cancellationToken = default) =>
        runMongoTransaction(async (session, ct) =>
        {
            var webServiceFilter = WebServiceFilters.InLifecycle<DeletingWebServiceLifecycle>(
                tenantId,
                webServiceId);
            var webService = await webServices.Find(session, webServiceFilter)
                .FirstOrDefaultAsync(ct);
            if (webService is null)
                return Result.Success();

            var readiness = await getResourceDeploymentFinalizationReadiness(
                tenantId,
                WebServiceResourceDeployment.ResourceType,
                webServiceId,
                session,
                ct);
            if (readiness is not ResourceDeploymentFinalizationReadiness.Ready)
            {
                return Result.Success();
            }

            var deleted = await webServices.DeleteOneAsync(
                session,
                filter: webServiceFilter,
                cancellationToken: ct);
            if (deleted.DeletedCount != 1)
                return Result.Success();

            var bindingCleanup = await deleteHostnameBindingsForResource(
                tenantId, WebServiceResourceDeployment.ResourceType, webServiceId, session, ct);
            if (bindingCleanup is not ISuccess)
                return (Failure)bindingCleanup;

            var purge = await purgeDeletedResourceDeployments(
                tenantId,
                WebServiceResourceDeployment.ResourceType,
                webServiceId,
                session,
                ct);
            if (purge is not ISuccess)
                return (Failure)purge;
            await storeOutboxWrites(
                session,
                [OutboxWrite.Create(webService.ToDeleteEvent())],
                ct);
            return Result.Success();
        }, cancellationToken);
}
