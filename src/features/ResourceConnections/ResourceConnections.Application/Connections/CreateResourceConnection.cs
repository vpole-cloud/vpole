using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceConnections.Application.Common;
using Pole.Features.ResourceConnections.Application.Declarations;
using Pole.Features.ResourceConnections.Application.Mongo;
using Pole.Features.ResourceConnections.Application.Recovery;
using Pole.Features.ResourceConnections.Application.References;
using Pole.Features.ResourceConnections.Application.Topology;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceConnections.Events;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Workloads;

namespace Pole.Features.ResourceConnections.Application.Connections;

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
    ResourceConnectionTopologyAdmission admission,
    IMongoCollection<ResourceConnection> connections,
    StoreOutboxWrites storeOutboxWrites)
{
    public Task<CreateResourceConnectionPersistenceOutcome> InvokeAsync(
        CreateResourceConnectionCommand command,
        CancellationToken cancellationToken = default) =>
        transactions(async (transaction, ct) =>
        {
            var result = await CreateAsync(new(ObjectId.GenerateNewId().ToString(), command), transaction.Session, ct);
            return result is ResourceConnection ? transaction.Commit(result) : transaction.Abort(result);
        }, cancellationToken);

    public async Task<CreateResourceConnectionPersistenceOutcome> CreateAsync(
        CreateResourceConnectionInSession request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Connection creation requires an active transaction.");

        var command = request.Command;
        var references = new[]
        {
            new FenceRegisteredResourceRequest(command.TenantId, command.SourceResourceType, command.SourceResourceId),
            new FenceRegisteredResourceRequest(command.TenantId,
                command.DestinationResourceType, command.DestinationResourceId)
        }.Distinct().OrderBy(item => item.ResourceTypeId.Value, StringComparer.Ordinal)
            .ThenBy(item => item.ResourceId, StringComparer.Ordinal);
        foreach (var reference in references)
        {
            if (await fenceRegistration(reference, session, cancellationToken) is null)
                return new ConnectionResourceMissing();
        }

        var admitted = await admission.ReadAsync(command, session, cancellationToken);
        return admitted switch
        {
            ResolvedResourceConnectionTopology topology => await PersistAsync(topology),
            ConnectionResourceMissing error => error,
            ConnectionEndpointUnsupported error => error,
            ConnectionSourceHasNoPlacements error => error,
            ConnectionPlacementDeleting error => error,
            ConnectionDestinationTargetMissing error => error,
            ConnectionDestinationTargetAmbiguous error => error
        };

        async Task<CreateResourceConnectionPersistenceOutcome> PersistAsync(ResolvedResourceConnectionTopology topology)
        {
            var connection = ResourceConnectionFactory.Create(request.Id, command.TenantId, command.Name,
                command.SourceResourceType, command.SourceResourceId, command.DestinationResourceType,
                command.DestinationResourceId, command.EndpointId, topology.Targets);
            try
            {
                await connections.InsertOneAsync(session, connection, cancellationToken: cancellationToken);
            }
            catch (MongoWriteException exception) when (MongoDuplicateKey.IsForIndex(exception,
                ResourceConnectionIndexes.TenantName,
                connections.CollectionNamespace.CollectionName))
            {
                return new ConnectionNameAlreadyExists(command.Name);
            }

            await storeOutboxWrites(session, [OutboxWrite.Create(new ResourceConnectionCreatedEvent(
                connection.Id, connection.Generation) { EventCore = EventCore.Create(command.TenantId) })],
                cancellationToken);
            return connection;
        }
    }
}

internal sealed record CreateResourceConnectionInSession(
    string Id,
    CreateResourceConnectionCommand Command);
