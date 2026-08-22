namespace Pole.Features.Postgres.Application.Persistence.Credentials

open System.Threading
open System.Threading.Tasks
open MongoDB.Driver
open Pole.Features.Postgres.Application.Persistence
open Pole.Features.Postgres.Domain

type LoadPostgresCredential =
    delegate of tenantId : string * clusterId : string * cancellationToken : CancellationToken -> Task<PostgresCredential option>

type MongoLoadPostgresCredential(credentials : IMongoCollection<PostgresCredential>) =
    member _.InvokeAsync(tenantId, clusterId, cancellationToken : CancellationToken) =
        task {
            PostgresPersistenceGuards.credentialIdentity tenantId clusterId
            let! credential =
                credentials
                    .Find(PostgresFilters.credentialByCluster tenantId clusterId)
                    .FirstOrDefaultAsync(cancellationToken)
            return if isNull (box credential) then None else Some credential
        }
