namespace Pole.Features.Postgres.Application.Persistence.Reads

open System.Collections.Immutable
open System.Threading
open MongoDB.Driver
open Pole.Features.Postgres.Application.Persistence
open Pole.Features.Postgres.Contracts
open Pole.Features.Postgres.Domain

type MongoLoadPostgresConnectionEndpoints(clusters : IMongoCollection<PostgresCluster>) =
    member _.InvokeAsync(tenantId, resourceId, session : IClientSessionHandle, cancellationToken : CancellationToken) =
        task {
            if not session.IsInTransaction then
                invalidOp "Connection endpoint lookup requires a transaction."
            let! resource =
                clusters.Find(session, PostgresFilters.byId tenantId resourceId)
                    .FirstOrDefaultAsync(cancellationToken)
            if isNull (box resource) then
                return LoadPostgresConnectionEndpointsOutcome(PostgresConnectionResourceMissing())
            elif resource.Lifecycle :? DeletingPostgresClusterLifecycle then
                return LoadPostgresConnectionEndpointsOutcome(PostgresConnectionResourceDeleting())
            else
                let endpoint = PostgresConnectionEndpointDto(
                    PostgresConnectionEndpoints.ReadWriteId,
                    PostgresConnectionEndpoints.TcpPort,
                    PostgresConnectionEndpoints.TcpPort)
                return LoadPostgresConnectionEndpointsOutcome(
                    PostgresConnectionEndpointsFound(
                        resource.Id,
                        PostgresConnectionEndpoints.BuildReadWriteServiceName(resource.Id),
                        ImmutableList.Create(endpoint)))
        }
