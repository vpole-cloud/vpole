using Pole.Features.ResourceConnections.Application.Topology;
using Pole.Features.ResourceConnections.Application.Recovery;
using Pole.Features.ResourceConnections.Application.Mongo;
using Pole.Features.ResourceConnections.Application.Declarations;
using Pole.Features.ResourceConnections.Application.Connections;
using Pole.Features.ResourceConnections.Application.Common;
using MongoDB.Driver;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Workloads;

namespace Pole.Features.ResourceConnections.Application.References;

internal sealed class ResourceConnectionReferencePlanning(
    IMongoCollection<ResourceConnection> connections,
    ResourceConnectionReferenceProvider references,
    LoadResourceRegistrationPlanningSnapshot loadRegistration,
    FenceResourceRegistrationPlanningSnapshot fenceRegistration)
{
    public async Task<LoadResourceConnectionReferencePlanningOutcome> ReadAsync(
        AdmitResourceConnectionReferencesRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (await references.ReadAsync(request, session, cancellationToken)
            is ResourceConnectionReferencesUnavailable unavailable)
            return unavailable;

        var versions = new List<ResourceConnectionPlanningVersion>();
        var destinations = new List<ConnectionDestinationPlanningVersion>();
        foreach (var id in request.References.Select(item => item.ConnectionId).Distinct()
                     .Order(StringComparer.Ordinal))
        {
            var current = await connections.Find(session,
                ResourceConnectionFilters.ByTenantAndId(request.TenantId, id)).SingleAsync(cancellationToken);
            versions.Add(new(current.Id, current.Revision, current.ReferenceFence));
            var registration = await loadRegistration(new(request.TenantId,
                ResourceTypeId.Create(current.DestinationResourceTypeId), current.DestinationResourceId),
                session, cancellationToken);
            if (registration is null)
                return new ResourceConnectionReferencesUnavailable(id, "DestinationUnavailable");

            destinations.Add(new(registration.Resource.ResourceTypeId, registration.Resource.ResourceId,
                registration.Resource.RegistrationId, registration.ReferenceFence));
        }

        return new ResourceConnectionReferencePlanningSnapshot(request, versions,
            destinations.DistinctBy(item => item.RegistrationId)
                .OrderBy(item => item.RegistrationId, StringComparer.Ordinal).ToArray());
    }

    public async Task<FenceResourceConnectionReferencePlanningOutcome> FenceAsync(
        ResourceConnectionReferencePlanningSnapshot snapshot,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var read = await ReadAsync(snapshot.Request, session, cancellationToken);
        if (read is ResourceConnectionReferencesUnavailable unavailable)
            return unavailable;

        var current = (ResourceConnectionReferencePlanningSnapshot)read.Value;
        if (!current.Connections.SequenceEqual(snapshot.Connections) ||
            !current.Destinations.SequenceEqual(snapshot.Destinations))
            return new ResourceConnectionReferencePlanningChanged();

        foreach (var destination in snapshot.Destinations)
        {
            var registration = await loadRegistration(new(snapshot.Request.TenantId,
                destination.ResourceType, destination.ResourceId), session, cancellationToken);
            if (registration is null || await fenceRegistration(registration, session, cancellationToken)
                is not ResourceRegistrationSnapshotFenced)
                return new ResourceConnectionReferencePlanningChanged();
        }

        foreach (var connection in snapshot.Connections)
        {
            var filter = ResourceConnectionFilters.AtPlanningVersion(snapshot.Request.TenantId, connection);
            var write = await connections.UpdateOneAsync(session, filter,
                Builders<ResourceConnection>.Update.Inc(item => item.ReferenceFence, 1),
                cancellationToken: cancellationToken);
            if (write.ModifiedCount != 1)
                return new ResourceConnectionReferencePlanningChanged();
        }

        return new ResourceConnectionReferencePlanningFenced();
    }
}
