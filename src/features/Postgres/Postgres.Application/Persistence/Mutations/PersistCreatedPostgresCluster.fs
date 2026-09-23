namespace Pole.Features.Postgres.Application.Persistence.Mutations

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open MongoDB.Driver
open Pole.Database
open Pole.Features.Outbox.Contracts
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Persistence
open Pole.Features.Postgres.Domain

type PersistCreatedPostgresCluster =
    delegate of
        session : IClientSessionHandle *
        cluster : PostgresCluster *
        cancellationToken : CancellationToken -> Task

type MongoPersistCreatedPostgresCluster(
    clusters : IMongoCollection<PostgresCluster>,
    storeOutboxWrites : StoreOutboxWrites) =

    member _.InvokeAsync(session, cluster, cancellationToken : CancellationToken) =
        task {
            PostgresPersistenceGuards.transaction session "PostgreSQL creation"
            PostgresPersistenceGuards.cluster cluster
            do! clusters.InsertOneAsync(session, cluster, cancellationToken = cancellationToken)
            let writes : IReadOnlyCollection<OutboxWrite> =
                [| OutboxWrite.Create(PostgresClusterEvents.created cluster) |]
            do! storeOutboxWrites.Invoke(session, writes, cancellationToken)
        }
