using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Domains.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Persistence.Lifecycle;

public sealed record WebServiceDeletionFinalized;

public union FinalizeWebServiceDeletionOutcome(
    WebServiceDeletionFinalized,
    WebServiceFinalizationError);

public delegate Task<FinalizeWebServiceDeletionOutcome> FinalizeWebServiceDeletion(
    string tenantId,
    string webServiceId,
    CancellationToken cancellationToken = default);

internal sealed class MongoFinalizeWebServiceDeletion(
    IMongoCollection<WebService> webServices,
    GetResourceDeploymentFinalizationReadiness getResourceDeploymentFinalizationReadiness,
    PurgeDeletedResourceDeployments purgeDeletedResourceDeployments,
    DeleteHostnameBindingsForResource deleteHostnameBindingsForResource,
    RunMongoOutcomeTransaction<FinalizeWebServiceDeletionOutcome> runMongoTransaction,
    StoreOutboxWrites storeOutboxWrites)
{
    public Task<FinalizeWebServiceDeletionOutcome> InvokeAsync(
        string tenantId,
        string webServiceId,
        CancellationToken cancellationToken = default) =>
        runMongoTransaction(async (transaction, ct) =>
        {
            var webServiceFilter = WebServiceFilters.InLifecycle<DeletingWebServiceLifecycle>(
                tenantId,
                webServiceId);
            var webService = await webServices.Find(transaction.Session, webServiceFilter)
                .FirstOrDefaultAsync(ct);
            if (webService is null)
                return transaction.Commit(new WebServiceDeletionFinalized());

            var readiness = await getResourceDeploymentFinalizationReadiness(
                tenantId,
                WebServiceResourceDeployment.ResourceType,
                webServiceId,
                transaction.Session,
                ct);
            if (readiness is not ResourceDeploymentFinalizationReadiness.Ready)
            {
                return transaction.Commit(new WebServiceDeletionFinalized());
            }

            var deleted = await webServices.DeleteOneAsync(
                transaction.Session,
                filter: webServiceFilter,
                cancellationToken: ct);
            if (deleted.DeletedCount != 1)
                return transaction.Commit(new WebServiceDeletionFinalized());

            var bindingCleanup = await deleteHostnameBindingsForResource(
                tenantId,
                WebServiceResourceDeployment.ResourceType,
                webServiceId,
                transaction.Session,
                ct);
            if (!bindingCleanup.TryGetValue(out _, out var bindingCleanupError))
            {
                return transaction.Abort(new WebServiceFinalizationError(bindingCleanupError));
            }

            var purge = await purgeDeletedResourceDeployments(
                tenantId,
                WebServiceResourceDeployment.ResourceType,
                webServiceId,
                transaction.Session,
                ct);
            if (purge is Err<ResourceDeploymentFinalizationConflict> conflict)
            {
                return transaction.Abort(new WebServiceFinalizationError(conflict.Value));
            }
            await storeOutboxWrites(
                transaction.Session,
                [OutboxWrite.Create(webService.ToDeleteEvent())],
                ct);
            return transaction.Commit(new WebServiceDeletionFinalized());
        }, cancellationToken);
}
