// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.ResourceConnections.Application.Connections;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.ResourceConnections.Application.Topology;

internal sealed record ResolvedResourceConnectionTopology(
    IReadOnlyList<ConnectionTargetSnapshot> Targets
);

internal union ReadResourceConnectionTopologyOutcome(
    ResolvedResourceConnectionTopology,
    ConnectionResourceMissing,
    ConnectionEndpointUnsupported,
    ConnectionSourceHasNoPlacements,
    ConnectionPlacementDeleting,
    ConnectionDestinationTargetMissing,
    ConnectionDestinationTargetAmbiguous);

internal sealed class ResourceConnectionTopologyAdmission(
    LoadResourceDeploymentConnectionPlacements loadPlacements,
    ResourceConnectionEndpointCatalog endpoints
)
{
    public async Task<ReadResourceConnectionTopologyOutcome> ReadAsync(
        CreateResourceConnectionCommand command,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var published = await endpoints.ResolveAsync(
            command.TenantId,
            command.DestinationResourceType,
            command.DestinationResourceId,
            command.EndpointId,
            session,
            cancellationToken);
        return published switch
        {
            ResolvedConnectionEndpoint endpoint => await ResolveTargetsAsync(endpoint),
            ConnectionEndpointMissing => new ConnectionResourceMissing(),
            ConnectionDestinationDeleting => new ConnectionPlacementDeleting(),
            ConnectionEndpointUnsupportedByProvider => new ConnectionEndpointUnsupported(),
        };

        async Task<ReadResourceConnectionTopologyOutcome> ResolveTargetsAsync(ResolvedConnectionEndpoint endpoint)
        {
            var sources = (await loadPlacements(
                    new LoadResourceDeploymentConnectionPlacementsRequest(
                        command.TenantId,
                        command.SourceResourceType,
                        command.SourceResourceId),
                    session,
                    cancellationToken)).Where(item => !item.DeletionRequested)
                .ToArray();
            var destinations = (await loadPlacements(
                    new LoadResourceDeploymentConnectionPlacementsRequest(
                        command.TenantId,
                        command.DestinationResourceType,
                        command.DestinationResourceId),
                    session,
                    cancellationToken)).Where(item => !item.DeletionRequested)
                .ToArray();
            var topology = ResourceConnectionTopology.Evaluate(
                sources.Select(item => item.DeploymentTargetId),
                destinations.Select(item => item.DeploymentTargetId));
            switch (topology.Kind)
            {
                case ConnectionTopologyKind.SourceHasNoPlacements:
                    return new ConnectionSourceHasNoPlacements();
                case ConnectionTopologyKind.DestinationTargetMissing:
                    return new ConnectionDestinationTargetMissing(topology.TargetId!);
                case ConnectionTopologyKind.DestinationTargetAmbiguous:
                    return new ConnectionDestinationTargetAmbiguous(topology.TargetId!);
            }

            var targets = sources.GroupBy(item => item.DeploymentTargetId, StringComparer.Ordinal)
                .Select(group =>
                {
                    var destination = destinations.Single(item => item.DeploymentTargetId == group.Key);
                    return ConnectionTargetSnapshot.Create(
                        group.Key,
                        destination.DeploymentId,
                        destination.RuntimeCellId,
                        destination.KubernetesNamespace,
                        endpoint.ServiceName,
                        endpoint.ServicePort.Value,
                        endpoint.TargetPort.Value,
                        group.Select(item => new ConnectionSourcePlacement(
                            item.DeploymentId,
                            item.RuntimeCellId,
                            item.KubernetesNamespace)));
                })
                .OrderBy(item => item.TargetId, StringComparer.Ordinal)
                .ToArray();
            return new ResolvedResourceConnectionTopology(targets);
        }
    }
}
