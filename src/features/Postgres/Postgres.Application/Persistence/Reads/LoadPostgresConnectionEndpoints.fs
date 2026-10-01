namespace Pole.Features.Postgres.Application.Persistence.Reads

open System
open System.Collections.Immutable
open System.Threading
open MongoDB.Driver
open Pole.Features.Postgres.Application.Persistence
open Pole.Features.Postgres.Contracts
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common
open Pole.Features.ResourceConnections.Contracts
open Pole.Workloads

type MongoLoadPostgresConnectionEndpoints(clusters : IMongoCollection<PostgresCluster>) =
    interface IResourceConnectionDestinationNameCapability with
        member _.Kind = "postgres"
        member _.ResourceType = PostgresResourceDeployment.ResourceType

        member _.ResolveResourceIdAsync(tenantId, name, cancellationToken : CancellationToken) =
            task {
                let! matches =
                    clusters.Find(PostgresFilters.byName tenantId name)
                        .Limit(2)
                        .ToListAsync(cancellationToken)
                return
                    ResourceConnectionNameResolution.ResolveUniqueResourceId(
                        matches,
                        Func<PostgresCluster, bool>(fun resource ->
                            not (resource.Lifecycle :? DeletingPostgresClusterLifecycle)),
                        Func<PostgresCluster, string>(fun resource -> resource.Id))
            }

    interface IResourceConnectionDestinationCapability with
        member _.ResourceType = PostgresResourceDeployment.ResourceType

        member _.LoadAsync(tenantId, resourceId, session : IClientSessionHandle, cancellationToken : CancellationToken) =
            task {
                if not session.IsInTransaction then
                    invalidOp "Connection endpoint lookup requires a transaction."
                let! resource =
                    clusters.Find(session, PostgresFilters.byId tenantId resourceId)
                        .FirstOrDefaultAsync(cancellationToken)
                if isNull (box resource) then
                    return LoadResourceConnectionEndpointsOutcome(ResourceConnectionDestinationMissing())
                elif resource.Lifecycle :? DeletingPostgresClusterLifecycle then
                    return LoadResourceConnectionEndpointsOutcome(ResourceConnectionDestinationDeleting())
                else
                    let port = NetworkPort.Create(PostgresConnectionEndpoints.TcpPort)
                    let endpoint = ResourceConnectionEndpoint(
                        PostgresConnectionEndpoints.ReadWriteId,
                        PostgresConnectionEndpoints.BuildReadWriteServiceName(resource.Id),
                        port,
                        port)
                    return LoadResourceConnectionEndpointsOutcome(
                        ResourceConnectionEndpointsFound(ImmutableList.Create(endpoint)))
            }
