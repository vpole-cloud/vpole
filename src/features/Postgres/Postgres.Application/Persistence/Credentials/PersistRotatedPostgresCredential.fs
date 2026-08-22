namespace Pole.Features.Postgres.Application.Persistence.Credentials

open System.Threading
open System.Threading.Tasks
open MongoDB.Driver
open Pole.Features.Postgres.Application.Persistence
open Pole.Features.Postgres.Domain

type PersistRotatedPostgresCredential =
    delegate of session : IClientSessionHandle * current : PostgresCredential * replacement : PostgresCredential * cancellationToken : CancellationToken -> Task<PostgresOutcome<unit, PostgresCredentialError>>

type MongoPersistRotatedPostgresCredential(credentials : IMongoCollection<PostgresCredential>) =
    member _.InvokeAsync(session, current, replacement, cancellationToken : CancellationToken) =
        task {
            PostgresPersistenceGuards.transaction session "PostgreSQL credential rotation"
            PostgresPersistenceGuards.sameCredentialIdentity current replacement
            if replacement.Version <> current.Version + 1L then
                invalidOp "A PostgreSQL credential rotation must advance the version by one."

            let! persisted =
                credentials.ReplaceOneAsync(
                    session,
                    PostgresFilters.credentialAtVersion current,
                    replacement,
                    cancellationToken = cancellationToken)
            return
                if persisted.ModifiedCount = 1L then
                    PostgresSuccess ()
                else
                    PostgresError(PostgresCredentialRotationConflict current.PostgresClusterId)
        }
