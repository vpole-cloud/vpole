namespace Pole.Features.Postgres.Application.Persistence.Lifecycle

open System.Threading
open System.Threading.Tasks
open MongoDB.Driver
open Pole.Features.Postgres.Application.Persistence
open Pole.Features.Postgres.Domain

type PostgresClusterDeletionConflict =
    { ClusterId : string }

type BeginPostgresClusterDeletion =
    delegate of session : IClientSessionHandle * current : PostgresCluster * replacement : PostgresCluster * cancellationToken : CancellationToken -> Task<PostgresOutcome<unit, PostgresClusterDeletionConflict>>

type MongoBeginPostgresClusterDeletion(clusters : IMongoCollection<PostgresCluster>) =
    member _.InvokeAsync(session, current, replacement, cancellationToken : CancellationToken) =
        task {
            PostgresPersistenceGuards.transaction session "PostgreSQL deletion start"
            PostgresPersistenceGuards.deletionTransition current replacement
            let! persisted =
                clusters.ReplaceOneAsync(
                    session,
                    PostgresFilters.activeAtRevision current,
                    replacement,
                    cancellationToken = cancellationToken)
            return
                if persisted.ModifiedCount = 1L then
                    PostgresSuccess ()
                else
                    PostgresError { ClusterId = current.Id }
        }
