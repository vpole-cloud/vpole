using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.WebServices.Application.Common;
using MongoDB.Driver;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;

namespace Pole.Features.WebServices.Application.Persistence.Mutations;

public sealed record WebServiceUpdateAccepted;

public sealed record WebServiceUpdateConflict(string WebServiceId);

public sealed record WebServiceConnectionConflict(
    string WebServiceId,
    string ConnectionId);

public union PersistWebServiceUpdateResult(
    WebServiceConnectionConflict,
    WebServiceUpdateAccepted,
    WebServiceUpdateConflict);

public delegate Task<PersistWebServiceUpdateResult>
    PersistUpdatedWebServiceOutcome(
        IClientSessionHandle session,
        WebService current,
        WebService replacement,
        WebServiceUpdatedEvent updatedEvent,
        CancellationToken cancellationToken = default);

internal sealed class MongoPersistUpdatedWebService(
    IMongoCollection<WebService> webServices,
    StoreOutboxWrites storeOutboxWrites,
    FenceRegisteredResource fenceRegisteredResource,
    ReconcileConnectionsForResource reconcileConnections)
{
    public async Task<PersistWebServiceUpdateResult>
        InvokeOutcomeAsync(
            IClientSessionHandle session,
            WebService current,
            WebService replacement,
            WebServiceUpdatedEvent updatedEvent,
            CancellationToken cancellationToken = default)
    {
        WebServicePersistenceGuards.EnsureTransaction(session, "Web service update");
        WebServicePersistenceGuards.EnsureSameIdentity(current, replacement);
        if (!string.Equals(
                updatedEvent.WebServiceId,
                current.Id,
                StringComparison.Ordinal) ||
            !string.Equals(
                updatedEvent.EventCore.TenantId,
                current.TenantId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A web service update event must preserve tenant and resource identity.");
        }

        var registration = await fenceRegisteredResource(
            current.TenantId, WebServiceResourceDeployment.ResourceType, current.Id, session, cancellationToken);
        if (registration is null)
            return new WebServiceUpdateConflict(current.Id);

        var result = await webServices.ReplaceOneAsync(
            session,
            WebServiceFilters.AtRevision(current),
            replacement,
            cancellationToken: cancellationToken);
        if (result.ModifiedCount != 1)
        {
            return new WebServiceUpdateConflict(current.Id);
        }

        var connections = await reconcileConnections(
            current.TenantId, WebServiceResourceDeployment.ResourceType, current.Id, session, cancellationToken);
        if (connections is ResourceConnectionTopologyConflict conflict)
            return new WebServiceConnectionConflict(current.Id, conflict.ConnectionId);

        await storeOutboxWrites(
            session,
            [OutboxWrite.Create(updatedEvent)],
            cancellationToken);
        return new WebServiceUpdateAccepted();
    }
}
