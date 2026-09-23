namespace Pole.Features.Postgres.Application.Persistence.Credentials

open System.Threading
open System.Threading.Tasks
open MongoDB.Driver
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Persistence
open Pole.Features.Secrets.Contracts

type PersistRotatedPostgresCredential =
    delegate of
        session : IClientSessionHandle *
        current : ManagedSecretValues *
        replacement : PreparedManagedSecret *
        cancellationToken : CancellationToken -> Task<PostgresOutcome<unit, PostgresCredentialError>>

type ManagedPersistRotatedPostgresCredential(rotateSecret : RotateManagedSecret) =
    member _.InvokeAsync(session, current : ManagedSecretValues, replacement : PreparedManagedSecret, cancellationToken : CancellationToken) =
        task {
            let! result = rotateSecret.Invoke(replacement, current.Version, session, cancellationToken)
            match TypedResultInterop.toResult result with
            | Ok _ -> return PostgresSuccess ()
            | Error error -> return PostgresError(PostgresCredentialErrors.fromManaged replacement.Owner.ResourceId error)
        }
