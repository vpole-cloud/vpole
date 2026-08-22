namespace Pole.Features.Postgres.Application.Persistence.Reads

open System.Threading
open System.Threading.Tasks
open MongoDB.Driver
open Pole.Features.Postgres.Application.Persistence
open Pole.Features.Postgres.Domain

type LoadPostgresClusterById =
    delegate of tenantId : string * clusterId : string * cancellationToken : CancellationToken -> Task<PostgresCluster option>

type MongoLoadPostgresClusterById(clusters : IMongoCollection<PostgresCluster>) =
    member _.InvokeAsync(tenantId, clusterId, cancellationToken : CancellationToken) =
        task {
            PostgresPersistenceGuards.identity tenantId clusterId
            let! cluster =
                clusters
                    .Find(PostgresFilters.byId tenantId clusterId)
                    .FirstOrDefaultAsync(cancellationToken)
            return if isNull (box cluster) then None else Some cluster
        }
