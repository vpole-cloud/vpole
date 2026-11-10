using System.Collections.Immutable;
using System.Globalization;
using MongoDB.Driver;
using Pole.Features.ResourceConnections.Application.Kubernetes;
using Pole.Features.ResourceConnections.Application.Mongo;
using Pole.Features.ResourceConnections.Application.Topology;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Workloads;

namespace Pole.Features.ResourceConnections.Application.References;

internal sealed class ResourceConnectionReferenceProvider(
    IMongoCollection<ResourceConnection> connections,
    ResourceConnectionEndpointCatalog endpoints,
    LoadResourceDeploymentConnectionPlacements loadPlacements,
    LoadExactResourceDeploymentRuntimePlacement loadPlacement
)
{
    public Task<AdmitResourceConnectionReferencesOutcome> AdmitAsync(
        AdmitResourceConnectionReferencesRequest request,
        IClientSessionHandle session,
        CancellationToken ct) => InspectAsync(request, session, ct, ReferenceAccessMode.AcquireFence);

    internal Task<AdmitResourceConnectionReferencesOutcome> ReadAsync(
        AdmitResourceConnectionReferencesRequest request,
        IClientSessionHandle session,
        CancellationToken ct) => InspectAsync(request, session, ct, ReferenceAccessMode.ReadOnly);

    private async Task<AdmitResourceConnectionReferencesOutcome> InspectAsync(
        AdmitResourceConnectionReferencesRequest request,
        IClientSessionHandle session,
        CancellationToken ct,
        ReferenceAccessMode access)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Connection reference admission requires a transaction.");
        if (request.References.Count == 0)
            return new ResourceConnectionReferencesAdmitted();
        var placements = (await loadPlacements(
                new LoadResourceDeploymentConnectionPlacementsRequest(
                    TenantId: request.TenantId,
                    ResourceType: request.SourceType,
                    ResourceId: request.SourceId),
                session: session,
                cancellationToken: ct)).Where(item => !item.DeletionRequested)
            .ToArray();
        foreach (var group in request.References.GroupBy(reference => reference.ConnectionId, StringComparer.Ordinal))
        {
            var current = await connections.Find(
                    session,
                    ResourceConnectionFilters.ByTenantAndId(request.TenantId, group.Key))
                .FirstOrDefaultAsync(ct);
            if (current is null || current.SourceResourceTypeId != request.SourceType.Value ||
                current.SourceResourceId != request.SourceId ||
                current.Lifecycle is not ActiveResourceConnection active)
                return Unavailable(group.Key, "ConnectionUnavailable");
            if (placements.Length == 0 ||
                placements.Any(placement => !active.Targets.Any(target => Covers(target.Snapshot, placement))))
                return Unavailable(group.Key, "DeploymentCoverageUnavailable");
            var endpoint = await endpoints.ResolveAsync(
                request.TenantId,
                ResourceTypeId.Create(current.DestinationResourceTypeId),
                current.DestinationResourceId,
                current.EndpointId,
                session,
                ct);
            if (endpoint is not ResolvedConnectionEndpoint resolved ||
                group.Any(reference => !ValidField(reference, resolved.Configuration)))
                return Unavailable(group.Key, "EndpointFieldUnavailable");
            if (active.Targets.Any(target =>
                    target.Snapshot.DestinationServiceName != resolved.ServiceName ||
                    target.Snapshot.DestinationServicePort != resolved.ServicePort.Value ||
                    target.Snapshot.DestinationPort != resolved.TargetPort.Value))
                return Unavailable(group.Key, "EndpointChanged");
            if (access == ReferenceAccessMode.AcquireFence)
                await connections.UpdateOneAsync(
                    session,
                    ResourceConnectionFilters.ByTenantAndId(request.TenantId, current.Id),
                    Builders<ResourceConnection>.Update.Inc(item => item.ReferenceFence, 1),
                    cancellationToken: ct);
        }

        return new ResourceConnectionReferencesAdmitted();
    }

    public async Task<ResolveResourceConnectionReferencesOutcome> ResolveAsync(
        ResolveResourceConnectionReferencesRequest request,
        CancellationToken ct)
    {
        if (request.References.Count == 0)
            return new ResolvedResourceConnectionValues([]);
        var placement = await loadPlacement(
            new LoadExactResourceDeploymentRuntimePlacementRequest(
                TenantId: request.TenantId,
                ResourceType: request.SourceType,
                ResourceId: request.SourceId,
                DeploymentId: request.DeploymentId),
            cancellationToken: ct);
        if (placement is null || placement.DesiredOperation == ResourceDeploymentDesiredOperation.Delete)
            return Unavailable(request.References.First().ConnectionId, "SourceDeploymentUnavailable");
        var values = ImmutableArray.CreateBuilder<ResolvedResourceConnectionValue>();
        foreach (var group in request.References
                     .Distinct()
                     .GroupBy(reference => reference.ConnectionId, StringComparer.Ordinal))
        {
            var current = await connections.Find(ResourceConnectionFilters.ByTenantAndId(request.TenantId, group.Key))
                .FirstOrDefaultAsync(ct);
            if (current is null || current.SourceResourceTypeId != request.SourceType.Value ||
                current.SourceResourceId != request.SourceId ||
                current.Lifecycle is not ActiveResourceConnection active)
                return Unavailable(group.Key, "ConnectionUnavailable");
            var matches = active.Targets
                .Where(target => target.Snapshot.TargetId == placement.DeploymentTargetId &&
                    target.Snapshot.Sources.Any(source =>
                        source.ResourceDeploymentId == request.DeploymentId &&
                        source.RuntimeCellId == placement.RuntimeCellId &&
                        source.Namespace == placement.KubernetesNamespace))
                .ToArray();
            if (matches.Length != 1)
                return Unavailable(group.Key, "DeploymentCoverageUnavailable");
            var snapshot = matches[0].Snapshot;
            var published = await endpoints.LoadAvailableAsync(
                request.TenantId,
                ResourceTypeId.Create(current.DestinationResourceTypeId),
                current.DestinationResourceId,
                ct);
            var endpoint = published?.Endpoints.SingleOrDefault(item => item.Id == current.EndpointId);
            if (endpoint is null || group.Any(reference => !ValidField(reference, endpoint.Configuration)))
                return Unavailable(group.Key, "EndpointFieldUnavailable");
            if (snapshot.DestinationServiceName != endpoint.ServiceName ||
                snapshot.DestinationServicePort != endpoint.ServicePort.Value ||
                snapshot.DestinationPort != endpoint.TargetPort.Value)
                return Unavailable(group.Key, "EndpointChanged");
            foreach (var reference in group)
            {
                var value = reference.ValueKind switch
                {
                    "Host" => ResourceConnectionServiceDns.Build(
                        snapshot.DestinationServiceName,
                        snapshot.DestinationNamespace),
                    "Port" => snapshot.DestinationServicePort.ToString(CultureInfo.InvariantCulture),
                    "Configuration" => endpoint.Configuration.Single(field => field.Key == reference.ConfigurationKey)
                        .Value,
                    _ => throw new InvalidOperationException("Unknown admitted connection field."),
                };
                values.Add(new ResolvedResourceConnectionValue(reference, value));
            }

            // A deletion or refreshed topology during provider discovery must not authorize a stale snapshot.
            var latest = await connections.Find(ResourceConnectionFilters.ByTenantAndId(request.TenantId, current.Id))
                .FirstOrDefaultAsync(ct);
            if (latest?.Generation != current.Generation || latest.Lifecycle is not ActiveResourceConnection)
                return Unavailable(group.Key, "ConnectionChanged");
        }

        return new ResolvedResourceConnectionValues(values.ToImmutable());
    }

    private static bool Covers(ConnectionTargetSnapshot target, ResourceDeploymentConnectionPlacement placement) =>
        target.TargetId == placement.DeploymentTargetId && target.Sources.Any(source =>
            source.ResourceDeploymentId == placement.DeploymentId && source.RuntimeCellId == placement.RuntimeCellId &&
            source.Namespace == placement.KubernetesNamespace);

    private static bool ValidField(
        ResourceConnectionValueReference reference,
        IReadOnlyList<ResourceConnectionConfigurationField> configuration) => reference.ValueKind switch
    {
        "Host" or "Port" => reference.ConfigurationKey is null,
        "Configuration" => configuration.Any(field => field.Key == reference.ConfigurationKey),
        _ => false,
    };

    private static ResourceConnectionReferencesUnavailable Unavailable(string id, string reason) => new(id, reason);

    private enum ReferenceAccessMode { ReadOnly, AcquireFence }
}
