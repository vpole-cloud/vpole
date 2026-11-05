namespace Pole.Features.Postgres.Application.Clusters

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open MongoDB.Driver
open Pole.Database
open Pole.Features.Outbox.Contracts
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application
open Pole.Features.Postgres.Application.Mongo
open Pole.Features.Postgres.Domain

type PostgresClusterNameAlreadyExists =
    { Name : string }

type PersistCreatedPostgresCluster =
    delegate of
        session : IClientSessionHandle *
        cluster : PostgresCluster *
        cancellationToken : CancellationToken -> Task<PostgresOutcome<unit, PostgresClusterNameAlreadyExists>>

type MongoPersistCreatedPostgresCluster(
    clusters : IMongoCollection<PostgresCluster>,
    storeOutboxWrites : StoreOutboxWrites) =

    member _.InvokeAsync(session, cluster, cancellationToken : CancellationToken) =
        task {
            PostgresPersistenceGuards.transaction session "PostgreSQL creation"
            PostgresPersistenceGuards.cluster cluster
            let! inserted = task {
                try
                    do! clusters.InsertOneAsync(session, cluster, cancellationToken = cancellationToken)
                    return true
                with
                | :? MongoWriteException as ex when ex.WriteError.Category = ServerErrorCategory.DuplicateKey ->
                    return false
            }
            if not inserted then
                return PostgresError { Name = cluster.Name }
            else
                let writes : IReadOnlyCollection<OutboxWrite> =
                    [| OutboxWrite.Create(PostgresClusterEvents.created cluster) |]
                do! storeOutboxWrites.Invoke(session, writes, cancellationToken)
                return PostgresSuccess ()
        }
