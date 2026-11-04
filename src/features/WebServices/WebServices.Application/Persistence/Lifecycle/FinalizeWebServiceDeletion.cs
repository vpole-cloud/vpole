using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Domains.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
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
    IResourceRegistrar resourceRegistrar,
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
                new GetResourceDeploymentFinalizationReadinessRequest(
                    TenantId: tenantId,
                    ResourceType: WebServiceResourceDeployment.ResourceType,
                    ResourceId: webServiceId),
                session: transaction.Session,
                cancellationToken: ct);
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

            var registration = await resourceRegistrar.UnregisterAsync(
                new UnregisterResourceRequest(
                    TenantId: tenantId,
                    ResourceGroupId: webService.ResourceGroupId,
                    ResourceTypeId: WebServiceResourceDeployment.ResourceType,
                    ResourceId: webServiceId),
                session: transaction.Session,
                cancellationToken: ct);
            if (registration is ResourceRegistrationMutationError registrationError)
            {
                return transaction.Abort(new WebServiceFinalizationError(registrationError));
            }

            var bindingCleanup = await deleteHostnameBindingsForResource(
                new DeleteHostnameBindingsForResourceRequest(
                    TenantId: tenantId,
                    ResourceType: WebServiceResourceDeployment.ResourceType,
                    ResourceId: webServiceId),
                session: transaction.Session,
                cancellationToken: ct);
            if (bindingCleanup is HostnameBindingCleanupError bindingCleanupError)
            {
                return transaction.Abort(new WebServiceFinalizationError(bindingCleanupError));
            }

            var purge = await purgeDeletedResourceDeployments(
                new PurgeDeletedResourceDeploymentsRequest(
                    TenantId: tenantId,
                    ResourceType: WebServiceResourceDeployment.ResourceType,
                    ResourceId: webServiceId),
                session: transaction.Session,
                cancellationToken: ct);
            if (purge is ResourceDeploymentFinalizationConflict conflict)
            {
                return transaction.Abort(new WebServiceFinalizationError(conflict));
            }
            await storeOutboxWrites(
                transaction.Session,
                [OutboxWrite.Create(webService.ToDeleteEvent())],
                ct);
            return transaction.Commit(new WebServiceDeletionFinalized());
        }, cancellationToken);
}
