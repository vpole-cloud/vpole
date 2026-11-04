using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.WebServices.Application.Common;
using MongoDB.Driver;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;

namespace Pole.Features.WebServices.Application.Persistence.Mutations;

public sealed record WebServiceUpdateAccepted;

public enum WebServiceUpdateConflictReason { ExpectedRevisionConflict, GitOwnedConfiguration }

public sealed record WebServiceUpdateConflict(
    string WebServiceId,
    WebServiceUpdateConflictReason Reason = WebServiceUpdateConflictReason.ExpectedRevisionConflict);

public sealed record WebServiceConnectionConflict(
    string WebServiceId,
    string ConnectionId);

public union WebServiceDependencyFailure(
    ResourceConnectionReferencesUnavailable,
    ConsumerSecretReferenceUnavailable);

public sealed record WebServiceDependencyUnavailable(
    string WebServiceId,
    WebServiceDependencyFailure Cause);

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
        CancellationToken cancellationToken = default,
        GitMutationContext? mutation = null);

internal sealed class MongoPersistUpdatedWebService(
    IMongoCollection<WebService> webServices,
    StoreOutboxWrites storeOutboxWrites,
    FenceRegisteredResource fenceRegisteredResource,
    ReconcileConnectionsForResource reconcileConnections,
    AdmitResourceConnectionReferences admitConnectionReferences,
    AdmitConsumerSecretReferences admitSecretReferences,
    IGitOpsMutations git)
{
    public async Task<PersistWebServiceUpdateResult>
        InvokeOutcomeAsync(
            IClientSessionHandle session,
            WebService current,
            WebService replacement,
            WebServiceUpdatedEvent updatedEvent,
            CancellationToken cancellationToken = default,
            GitMutationContext? mutation = null)
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

        if (!await git.AuthorizeAsync(
            new AuthorizeGitResourceMutationRequest(
                TenantId: current.TenantId,
                Kind: "WebService",
                ResourceId: current.Id,
                Context: mutation),
            session: session,
            ct: cancellationToken))
            return new WebServiceUpdateConflict(current.Id, WebServiceUpdateConflictReason.GitOwnedConfiguration);
        var registration = await fenceRegisteredResource(
            new FenceRegisteredResourceRequest(
                TenantId: current.TenantId,
                ResourceTypeId: WebServiceResourceDeployment.ResourceType,
                ResourceId: current.Id),
            session: session,
            cancellationToken: cancellationToken);
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
            new ReconcileConnectionsForResourceRequest(
                TenantId: current.TenantId,
                ResourceType: WebServiceResourceDeployment.ResourceType,
                ResourceId: current.Id),
            session: session,
            cancellationToken: cancellationToken);
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
            var admittedConnections = await admitConnectionReferences(
                new AdmitResourceConnectionReferencesRequest(
                    TenantId: current.TenantId,
                    SourceType: WebServiceResourceDeployment.ResourceType,
                    SourceId: current.Id,
                    References: replacement.Spec.Config.ConnectionEnvironmentReferences.Values.Select(reference => new ResourceConnectionValueReference(reference.ConnectionId, reference.ValueKind, reference.ConfigurationKey)).ToArray()),
                session: session,
                ct: cancellationToken);
            if (admittedConnections is ResourceConnectionReferencesUnavailable unavailable)
                return new WebServiceDependencyUnavailable(current.Id, unavailable);
        }
        var admittedSecrets = await admitSecretReferences(
            new AdmitConsumerSecretReferencesRequest(
                TenantId: current.TenantId,
                ResourceGroupId: current.ResourceGroupId,
                IsolationScopeId: current.IsolationScopeId,
                ConsumerType: WebServiceResourceDeployment.ResourceType,
                ConsumerId: current.Id,
                PreviousReferences: current.Spec.Config.SecretEnvironmentReferences.Values.Select(reference => new ConsumerSecretKeyReference(reference.SecretId, reference.Key)).ToArray(),
                DesiredReferences: replacement.Spec.Config.SecretEnvironmentReferences.Values.Select(reference => new ConsumerSecretKeyReference(reference.SecretId, reference.Key)).ToArray(),
                Availability: cleanup ? ConsumerSecretAvailabilityPolicy.AllowUnavailableForCleanup : ConsumerSecretAvailabilityPolicy.RequireAvailable,
                Retention: mutation is null ? PreviousSecretReferenceRetention.DoNotRetain : PreviousSecretReferenceRetention.KeepUntilRolloutConfirmed),
            session: session,
            ct: cancellationToken);
        if (admittedSecrets is ConsumerSecretReferenceUnavailable secretUnavailable)
            return new WebServiceDependencyUnavailable(current.Id, secretUnavailable);

        await storeOutboxWrites(
            session,
            [OutboxWrite.Create(updatedEvent)],
            cancellationToken);
        return new WebServiceUpdateAccepted();
    }
}
