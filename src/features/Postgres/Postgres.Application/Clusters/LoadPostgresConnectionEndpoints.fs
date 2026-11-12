namespace Pole.Features.Postgres.Application.Clusters

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Threading
open MongoDB.Driver
open Pole.Features.Postgres.Application.Mongo
open Pole.Features.Postgres.Application.Connections
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common
open Pole.Features.ResourceConnections.Contracts
open Pole.Workloads

type MongoLoadPostgresConnectionEndpoints(clusters: IMongoCollection<PostgresCluster>) =
    let endpoints (resource: PostgresCluster) =
        let port = NetworkPort.Create(PostgresConnectionEndpoints.TcpPort)

        let endpoint =
            ResourceConnectionEndpoint(
                PostgresConnectionEndpoints.ReadWriteId,
                PostgresConnectionEndpoints.buildReadWriteServiceName (resource.Id),
                port,
                port,
                DisplayName = "PostgreSQL read/write",
                Protocol = "postgresql",
                Configuration =
                    ImmutableList.Create(
                        ResourceConnectionConfigurationField("database", "Database", PostgresManagedSecret.administrator))
            )

        ResourceConnectionEndpointsFound(ImmutableList.Create(endpoint))

    interface IResourceConnectionDestinationNameCapability with
        member _.Kind = "postgres"
        member _.ResourceType = PostgresResourceDeployment.ResourceType

        member _.ResolveResourceIdAsync(tenantId, name, cancellationToken: CancellationToken) =
            task {
                let! matches =
                    clusters.Find(PostgresFilters.byName tenantId name).Limit(2).ToListAsync(cancellationToken)

                return
                    ResourceConnectionNameResolution.ResolveUniqueResourceId(
                        matches,
                        Func<PostgresCluster, bool>(fun resource ->
                            not (resource.Lifecycle :? DeletingPostgresClusterLifecycle)),
                        Func<PostgresCluster, string>(fun resource -> resource.Id)
                    )
            }

    interface IResourceConnectionDestinationCapability with
        member _.ResourceType = PostgresResourceDeployment.ResourceType

        member _.LoadAsync
            (
                request: LoadResourceConnectionDestinationRequest,
                session: IClientSessionHandle,
                cancellationToken: CancellationToken
            ) =
            task {
                if not session.IsInTransaction then
                    invalidOp "Connection endpoint lookup requires a transaction."

                let! resource =
                    clusters
                        .Find(session, PostgresFilters.byId request.TenantId request.ResourceId)
                        .FirstOrDefaultAsync(cancellationToken)

                if isNull (box resource) then
                    return LoadResourceConnectionEndpointsOutcome(ResourceConnectionDestinationMissing())
                elif resource.Lifecycle :? DeletingPostgresClusterLifecycle then
                    return LoadResourceConnectionEndpointsOutcome(ResourceConnectionDestinationDeleting())
                else
                    return LoadResourceConnectionEndpointsOutcome(endpoints resource)
            }

        member _.LoadAvailableAsync
            (tenantId, resourceIds: IReadOnlyCollection<string>, cancellationToken: CancellationToken)
            =
            task {
                let found =
                    Dictionary<string, ResourceConnectionEndpointsFound>(StringComparer.Ordinal)

                if resourceIds.Count > 0 then
                    let! resources =
                        clusters.Find(PostgresFilters.byIds tenantId resourceIds).ToListAsync(cancellationToken)

                    for resource in resources do
                        if not (resource.Lifecycle :? DeletingPostgresClusterLifecycle) then
                            found.Add(resource.Id, endpoints resource)

                return found :> IReadOnlyDictionary<string, ResourceConnectionEndpointsFound>
            }
