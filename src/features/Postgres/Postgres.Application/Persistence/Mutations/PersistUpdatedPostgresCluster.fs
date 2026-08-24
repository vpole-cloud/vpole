namespace Pole.Features.Postgres.Application.Persistence.Mutations

open System.Threading
open System.Threading.Tasks
open MongoDB.Driver
open Pole.Database
open Pole.Features.Outbox.Contracts
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Persistence
open Pole.Features.Postgres.Domain

type PostgresClusterUpdateConflict =
    { ClusterId : string }

type PersistUpdatedPostgresCluster =
    delegate of session : IClientSessionHandle * current : PostgresCluster * replacement : PostgresCluster * cancellationToken : CancellationToken -> Task<PostgresOutcome<unit, PostgresClusterUpdateConflict>>

type MongoPersistUpdatedPostgresCluster(
    clusters : IMongoCollection<PostgresCluster>,
    storeOutboxWrites : StoreOutboxWrites) =

    member _.InvokeAsync(session, current, replacement, cancellationToken : CancellationToken) =
        task {
            PostgresPersistenceGuards.transaction session "PostgreSQL update"
            PostgresPersistenceGuards.activeUpdate current replacement
            let! persisted =
                clusters.ReplaceOneAsync(
                    session,
                    PostgresFilters.activeAtRevision current,
                    replacement,
                    cancellationToken = cancellationToken)

            if persisted.ModifiedCount <> 1L then
                return PostgresError { ClusterId = current.Id }
            else
                let writes =
                    [| OutboxWrite.Create(PostgresClusterEvents.updated current replacement) |]
                do! storeOutboxWrites.Invoke(session, writes, cancellationToken)
                return PostgresSuccess ()
        }
