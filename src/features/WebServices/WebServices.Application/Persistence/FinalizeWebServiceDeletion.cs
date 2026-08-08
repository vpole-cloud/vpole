using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceDeployments.Application.Persistence;
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
    IMongoCollection<ResourceDeployment> resourceDeployments,
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

            var deploymentFilter = ResourceDeploymentFilters.ForResource(
                tenantId,
                WebServiceResourceDeployment.ResourceType,
                webServiceId);
            var deployments = await resourceDeployments.Find(session, deploymentFilter)
                .ToListAsync(ct);
            if (deployments.Count == 0 ||
                deployments.Any(item => item.State is not DeletedResourceDeploymentState))
            {
                return Result.Success();
            }

            var deleted = await webServices.DeleteOneAsync(
                session,
                filter: webServiceFilter,
                cancellationToken: ct);
            if (deleted.DeletedCount != 1)
                return Result.Success();

            await resourceDeployments.DeleteManyAsync(
                session,
                filter: deploymentFilter,
                cancellationToken: ct);
            await storeOutboxWrites(
                session,
                [OutboxWrite.Create(webService.ToDeleteEvent())],
                ct);
            return Result.Success();
        }, cancellationToken);
}
