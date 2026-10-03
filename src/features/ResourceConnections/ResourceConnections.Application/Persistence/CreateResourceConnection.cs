using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceConnections.Events;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Events.Abstractions;
using Pole.Workloads;

namespace Pole.Features.ResourceConnections.Application.Persistence;

public sealed record CreateResourceConnectionCommand(
    string TenantId,
    string Name,
    ResourceTypeId SourceResourceType,
    string SourceResourceId,
    ResourceTypeId DestinationResourceType,
    string DestinationResourceId,
    string EndpointId);

public sealed record ConnectionResourceMissing;
public sealed record ConnectionEndpointUnsupported;
public sealed record ConnectionSourceHasNoPlacements;
public sealed record ConnectionPlacementDeleting;
public sealed record ConnectionDestinationTargetMissing(string TargetId);
public sealed record ConnectionDestinationTargetAmbiguous(string TargetId);
public sealed record ConnectionNameAlreadyExists(string Name);

public union CreateResourceConnectionPersistenceOutcome(
    ResourceConnection,
    ConnectionResourceMissing,
    ConnectionEndpointUnsupported,
    ConnectionSourceHasNoPlacements,
    ConnectionPlacementDeleting,
    ConnectionDestinationTargetMissing,
    ConnectionDestinationTargetAmbiguous,
    ConnectionNameAlreadyExists);

public delegate Task<CreateResourceConnectionPersistenceOutcome> PersistResourceConnectionCreation(
    CreateResourceConnectionCommand command,
    CancellationToken cancellationToken = default);

internal sealed class MongoCreateResourceConnection(
    RunMongoOutcomeTransaction<CreateResourceConnectionPersistenceOutcome> transactions,
    FenceRegisteredResource fenceRegistration,
    LoadResourceDeploymentConnectionPlacements loadPlacements,
    ResourceConnectionEndpointCatalog endpointCatalog,
    IMongoCollection<ResourceConnection> connections,
    StoreOutboxWrites storeOutboxWrites)
{
    public Task<CreateResourceConnectionPersistenceOutcome> InvokeAsync(
        CreateResourceConnectionCommand command,
        CancellationToken cancellationToken = default) =>
        transactions(
            async (transaction, ct) =>
            {
                var source = new ResourceReference(
                    command.SourceResourceType,
                    command.SourceResourceId);
                var destination = new ResourceReference(
                    command.DestinationResourceType,
                    command.DestinationResourceId);
                var references = new[] { source, destination }
                    .Distinct()
                    .OrderBy(item => item.Type.Value, StringComparer.Ordinal)
                    .ThenBy(item => item.Id, StringComparer.Ordinal);
                // Placement and endpoint mutations acquire this same registration
                // write fence before changing topology in their transactions.
                foreach (var reference in references)
                {
                    var registered = await fenceRegistration(
                        command.TenantId,
                        reference.Type,
                        reference.Id,
                        transaction.Session,
                        ct);
                    if (registered is null)
                        return transaction.Abort(new CreateResourceConnectionPersistenceOutcome(
                            new ConnectionResourceMissing()));
                }

                var publishedEndpoint = await endpointCatalog.ResolveAsync(
                    command.TenantId,
                    destination.Type,
                    command.DestinationResourceId,
                    command.EndpointId,
                    transaction.Session,
                    ct);
                return publishedEndpoint switch
                {
                    ResolvedConnectionEndpoint endpoint => await PersistResolvedEndpointAsync(endpoint),
                    ConnectionEndpointMissing => transaction.Abort(new CreateResourceConnectionPersistenceOutcome(
                        new ConnectionResourceMissing())),
                    ConnectionDestinationDeleting => transaction.Abort(new CreateResourceConnectionPersistenceOutcome(
                        new ConnectionPlacementDeleting())),
                    ConnectionEndpointUnsupportedByProvider => transaction.Abort(
                        new CreateResourceConnectionPersistenceOutcome(new ConnectionEndpointUnsupported()))
                };

                async Task<MongoTransactionDecision<CreateResourceConnectionPersistenceOutcome>>
                    PersistResolvedEndpointAsync(ResolvedConnectionEndpoint endpoint)
                {
                    var sourcePlacements = (await loadPlacements(
                        command.TenantId,
                        source.Type,
                        source.Id,
                        transaction.Session,
                        ct))
                        .Where(item => !item.DeletionRequested)
                        .ToArray();
                    var destinationPlacements = (await loadPlacements(
                        command.TenantId,
                        destination.Type,
                        destination.Id,
                        transaction.Session,
                        ct))
                        .Where(item => !item.DeletionRequested)
                        .ToArray();

                    var topology = ResourceConnectionTopology.Evaluate(sourcePlacements.Select(item => item.DeploymentTargetId), destinationPlacements.Select(item => item.DeploymentTargetId));
                    switch (topology.Kind)
                    {
                        case ConnectionTopologyKind.SourceHasNoPlacements:
                            return transaction.Abort(new CreateResourceConnectionPersistenceOutcome(new ConnectionSourceHasNoPlacements()));
                        case ConnectionTopologyKind.DestinationTargetMissing:
                            return transaction.Abort(new CreateResourceConnectionPersistenceOutcome(new ConnectionDestinationTargetMissing(topology.TargetId!)));
                        case ConnectionTopologyKind.DestinationTargetAmbiguous:
                            return transaction.Abort(new CreateResourceConnectionPersistenceOutcome(new ConnectionDestinationTargetAmbiguous(topology.TargetId!)));
                    }
                    var snapshots = new List<ConnectionTargetSnapshot>();
                    foreach (var sourcesOnTarget in sourcePlacements
                        .GroupBy(item => item.DeploymentTargetId, StringComparer.Ordinal))
                    {
                        var destinationPlacement = destinationPlacements.Single(item => item.DeploymentTargetId == sourcesOnTarget.Key);
                        snapshots.Add(ConnectionTargetSnapshot.Create(
                            sourcesOnTarget.Key,
                            destinationPlacement.DeploymentId,
                            destinationPlacement.RuntimeCellId,
                            destinationPlacement.KubernetesNamespace,
                            endpoint.ServiceName,
                            endpoint.ServicePort.Value,
                            endpoint.TargetPort.Value,
                            sourcesOnTarget.Select(item => new ConnectionSourcePlacement(
                                item.DeploymentId,
                                item.RuntimeCellId,
                                item.KubernetesNamespace))));
                    }

                    var connection = ResourceConnectionFactory.Create(
                        ObjectId.GenerateNewId().ToString(),
                        command.TenantId,
                        command.Name,
                        source.Type,
                        source.Id,
                        destination.Type,
                        destination.Id,
                        endpoint.Id,
                        snapshots);
                    try
                    {
                        await connections.InsertOneAsync(
                            transaction.Session,
                            connection,
                            cancellationToken: ct);
                    }
                    catch (MongoWriteException exception) when (
                        exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
                    {
                        return transaction.Abort(new CreateResourceConnectionPersistenceOutcome(
                            new ConnectionNameAlreadyExists(command.Name)));
                    }

                    await storeOutboxWrites(
                        transaction.Session,
                        [OutboxWrite.Create(new ResourceConnectionCreatedEvent(
                        connection.Id,
                        connection.Generation)
                    {
                        EventCore = EventCore.Create(command.TenantId)
                    })],
                        ct);
                    return transaction.Commit(new CreateResourceConnectionPersistenceOutcome(connection));
                }
            },
            cancellationToken);

    private sealed record ResourceReference(
        ResourceTypeId Type,
        string Id);
}
