using System.Collections.Immutable;
using System.Globalization;
using MongoDB.Driver;
using Pole.Features.ResourceConnections.Application.Kubernetes;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Workloads;

namespace Pole.Features.ResourceConnections.Application.Persistence;

internal sealed class ResourceConnectionReferenceProvider(
    IMongoCollection<ResourceConnection> connections,
    ResourceConnectionEndpointCatalog endpoints,
    LoadResourceDeploymentConnectionPlacements loadPlacements,
    LoadExactResourceDeploymentRuntimePlacement loadPlacement)
{
    public async Task<AdmitResourceConnectionReferencesOutcome> AdmitAsync(
        string tenantId,
        ResourceTypeId sourceType,
        string sourceId,
        IReadOnlyCollection<ResourceConnectionValueReference> references,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (!session.IsInTransaction) throw new InvalidOperationException("Connection reference admission requires a transaction.");
        if (references.Count == 0) return new ResourceConnectionReferencesAdmitted();
        var placements = (await loadPlacements(tenantId, sourceType, sourceId, session, ct)).Where(item => !item.DeletionRequested).ToArray();
        foreach (var group in references.GroupBy(reference => reference.ConnectionId, StringComparer.Ordinal))
        {
            var current = await connections.Find(session, ResourceConnectionFilters.ByTenantAndId(tenantId, group.Key)).FirstOrDefaultAsync(ct);
            if (current is null || current.SourceResourceTypeId != sourceType.Value || current.SourceResourceId != sourceId || current.Lifecycle is not ActiveResourceConnection active)
                return Unavailable(group.Key, "ConnectionUnavailable");
            if (placements.Length == 0 || placements.Any(placement => !active.Targets.Any(target => Covers(target.Snapshot, placement))))
                return Unavailable(group.Key, "DeploymentCoverageUnavailable");
            var endpoint = await endpoints.ResolveAsync(tenantId, ResourceTypeId.Create(current.DestinationResourceTypeId), current.DestinationResourceId, current.EndpointId, session, ct);
            if (endpoint is not ResolvedConnectionEndpoint resolved || group.Any(reference => !ValidField(reference, resolved.Configuration)))
                return Unavailable(group.Key, "EndpointFieldUnavailable");
            if (active.Targets.Any(target => target.Snapshot.DestinationServiceName != resolved.ServiceName || target.Snapshot.DestinationServicePort != resolved.ServicePort.Value || target.Snapshot.DestinationPort != resolved.TargetPort.Value))
                return Unavailable(group.Key, "EndpointChanged");
            await connections.UpdateOneAsync(session, ResourceConnectionFilters.ByTenantAndId(tenantId, current.Id),
                Builders<ResourceConnection>.Update.Inc(item => item.ReferenceFence, 1), cancellationToken: ct);
        }
        return new ResourceConnectionReferencesAdmitted();
    }

    public async Task<ResolveResourceConnectionReferencesOutcome> ResolveAsync(
        string tenantId,
        ResourceTypeId sourceType,
        string sourceId,
        string deploymentId,
        IReadOnlyCollection<ResourceConnectionValueReference> references,
        CancellationToken ct)
    {
        if (references.Count == 0) return new ResolvedResourceConnectionValues([]);
        var placement = await loadPlacement(tenantId, sourceType, sourceId, deploymentId, ct);
        if (placement is null || placement.DesiredOperation == ResourceDeploymentDesiredOperation.Delete) return Unavailable(references.First().ConnectionId, "SourceDeploymentUnavailable");
        var values = ImmutableArray.CreateBuilder<ResolvedResourceConnectionValue>();
        foreach (var group in references.Distinct().GroupBy(reference => reference.ConnectionId, StringComparer.Ordinal))
        {
            var current = await connections.Find(ResourceConnectionFilters.ByTenantAndId(tenantId, group.Key)).FirstOrDefaultAsync(ct);
            if (current is null || current.SourceResourceTypeId != sourceType.Value || current.SourceResourceId != sourceId || current.Lifecycle is not ActiveResourceConnection active)
                return Unavailable(group.Key, "ConnectionUnavailable");
            var matches = active.Targets.Where(target => target.Snapshot.TargetId == placement.DeploymentTargetId &&
                target.Snapshot.Sources.Any(source => source.ResourceDeploymentId == deploymentId && source.RuntimeCellId == placement.RuntimeCellId && source.Namespace == placement.KubernetesNamespace)).ToArray();
            if (matches.Length != 1) return Unavailable(group.Key, "DeploymentCoverageUnavailable");
            var snapshot = matches[0].Snapshot;
            var published = await endpoints.LoadAvailableAsync(tenantId, ResourceTypeId.Create(current.DestinationResourceTypeId), current.DestinationResourceId, ct);
            var endpoint = published?.Endpoints.SingleOrDefault(item => item.Id == current.EndpointId);
            if (endpoint is null || group.Any(reference => !ValidField(reference, endpoint.Configuration))) return Unavailable(group.Key, "EndpointFieldUnavailable");
            if (snapshot.DestinationServiceName != endpoint.ServiceName || snapshot.DestinationServicePort != endpoint.ServicePort.Value || snapshot.DestinationPort != endpoint.TargetPort.Value)
                return Unavailable(group.Key, "EndpointChanged");
            foreach (var reference in group)
            {
                var value = reference.ValueKind switch
                {
                    "Host" => ResourceConnectionServiceDns.Build(snapshot.DestinationServiceName, snapshot.DestinationNamespace),
                    "Port" => snapshot.DestinationServicePort.ToString(CultureInfo.InvariantCulture),
                    "Configuration" => endpoint.Configuration.Single(field => field.Key == reference.ConfigurationKey).Value,
                    _ => throw new InvalidOperationException("Unknown admitted connection field.")
                };
                values.Add(new(reference, value));
            }
            // A deletion or refreshed topology during provider discovery must not authorize a stale snapshot.
            var latest = await connections.Find(ResourceConnectionFilters.ByTenantAndId(tenantId, current.Id)).FirstOrDefaultAsync(ct);
            if (latest?.Generation != current.Generation || latest.Lifecycle is not ActiveResourceConnection)
                return Unavailable(group.Key, "ConnectionChanged");
        }
        return new ResolvedResourceConnectionValues(values.ToImmutable());
    }

    private static bool Covers(ConnectionTargetSnapshot target, ResourceDeploymentConnectionPlacement placement) =>
        target.TargetId == placement.DeploymentTargetId && target.Sources.Any(source => source.ResourceDeploymentId == placement.DeploymentId && source.RuntimeCellId == placement.RuntimeCellId && source.Namespace == placement.KubernetesNamespace);
    private static bool ValidField(ResourceConnectionValueReference reference, IReadOnlyList<ResourceConnectionConfigurationField> configuration) => reference.ValueKind switch
    {
        "Host" or "Port" => reference.ConfigurationKey is null,
        "Configuration" => configuration.Any(field => field.Key == reference.ConfigurationKey),
        _ => false
    };
    private static ResourceConnectionReferencesUnavailable Unavailable(string id, string reason) => new(id, reason);
}
