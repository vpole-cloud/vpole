using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.Secrets.Contracts;
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

public sealed record WebServiceDependencyUnavailable(
    string WebServiceId,
    string ReasonCode);

public union PersistWebServiceUpdateResult(
    WebServiceConnectionConflict,
    WebServiceDependencyUnavailable,
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
    ReconcileConnectionsForResource reconcileConnections,
    AdmitResourceConnectionReferences admitConnectionReferences,
    AdmitConsumerSecretReferences admitSecretReferences)
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
        var connectionConflict = connections switch
        {
            ResourceConnectionTopologyValid => null,
            ResourceConnectionSourceHasNoPlacements error =>
                new WebServiceConnectionConflict(current.Id, error.ConnectionId),
            ResourceConnectionDestinationTargetMissing error =>
                new WebServiceConnectionConflict(current.Id, error.ConnectionId),
            ResourceConnectionDestinationTargetAmbiguous error =>
                new WebServiceConnectionConflict(current.Id, error.ConnectionId),
            ResourceConnectionEndpointMissing error =>
                new WebServiceConnectionConflict(current.Id, error.ConnectionId),
            ResourceConnectionDestinationDeletingConflict error =>
                new WebServiceConnectionConflict(current.Id, error.ConnectionId),
            ResourceConnectionConcurrentMutation error =>
                new WebServiceConnectionConflict(current.Id, error.ConnectionId)
        };
        if (connectionConflict is not null)
            return connectionConflict;

        var cleanup = updatedEvent.Deployments.Any(item => item.Operation == "Delete") &&
            current.Spec.Config.ConnectionEnvironmentReferences.OrderBy(pair => pair.Key).SequenceEqual(replacement.Spec.Config.ConnectionEnvironmentReferences.OrderBy(pair => pair.Key)) &&
            current.Spec.Config.SecretEnvironmentReferences.OrderBy(pair => pair.Key).SequenceEqual(replacement.Spec.Config.SecretEnvironmentReferences.OrderBy(pair => pair.Key));
        if (!cleanup)
        {
            var admittedConnections = await admitConnectionReferences(current.TenantId, WebServiceResourceDeployment.ResourceType, current.Id,
                replacement.Spec.Config.ConnectionEnvironmentReferences.Values.Select(reference => new ResourceConnectionValueReference(reference.ConnectionId, reference.ValueKind, reference.ConfigurationKey)).ToArray(), session, cancellationToken);
            if (admittedConnections is ResourceConnectionReferencesUnavailable unavailable)
                return new WebServiceDependencyUnavailable(current.Id, unavailable.ReasonCode);
        }
        var admittedSecrets = await admitSecretReferences(current.TenantId, current.ResourceGroupId, current.IsolationScopeId, WebServiceResourceDeployment.ResourceType, current.Id,
            current.Spec.Config.SecretEnvironmentReferences.Values.Select(reference => new ConsumerSecretKeyReference(reference.SecretId, reference.Key)).ToArray(),
            replacement.Spec.Config.SecretEnvironmentReferences.Values.Select(reference => new ConsumerSecretKeyReference(reference.SecretId, reference.Key)).ToArray(),
            !cleanup, session, cancellationToken);
        if (admittedSecrets is ConsumerSecretReferenceUnavailable secretUnavailable)
            return new WebServiceDependencyUnavailable(current.Id, secretUnavailable.ReasonCode);

        await storeOutboxWrites(
            session,
            [OutboxWrite.Create(updatedEvent)],
            cancellationToken);
        return new WebServiceUpdateAccepted();
    }
}
