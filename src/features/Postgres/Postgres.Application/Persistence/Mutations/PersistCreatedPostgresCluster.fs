namespace Pole.Features.Postgres.Application.Persistence.Mutations

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open MongoDB.Driver
open Pole.Database
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Persistence
open Pole.Features.Postgres.Domain

type PersistCreatedPostgresCluster =
    delegate of session : IClientSessionHandle * cluster : PostgresCluster * credential : PostgresCredential * cancellationToken : CancellationToken -> Task

type MongoPersistCreatedPostgresCluster(
    clusters : IMongoCollection<PostgresCluster>,
    credentials : IMongoCollection<PostgresCredential>,
    storeOutboxWrites : StoreOutboxWrites) =

    member _.InvokeAsync(session, cluster, credential, cancellationToken : CancellationToken) =
        task {
            PostgresPersistenceGuards.transaction session "PostgreSQL creation"
            PostgresPersistenceGuards.cluster cluster
            PostgresPersistenceGuards.credential credential
            if cluster.TenantId <> credential.TenantId || cluster.Id <> credential.PostgresClusterId then
                invalidOp "A PostgreSQL credential must belong to the cluster being created."

            do! clusters.InsertOneAsync(session, cluster, cancellationToken = cancellationToken)
            do! credentials.InsertOneAsync(session, credential, cancellationToken = cancellationToken)
            let writes : IReadOnlyCollection<OutboxWrite> =
                [| OutboxWrite.Create(PostgresClusterEvents.created cluster) |]
            do! storeOutboxWrites.Invoke(session, writes, cancellationToken)
        }
