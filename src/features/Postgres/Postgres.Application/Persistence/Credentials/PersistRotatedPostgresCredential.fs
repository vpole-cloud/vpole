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
            let! result = rotateSecret.Invoke(
                RotateManagedSecretRequest(
                    replacement,
                    current.Version),
                session,
                cancellationToken)
            match result.Value with
            | :? ManagedSecretMutationSucceeded -> return PostgresSuccess ()
            | :? ManagedSecretFailure as error ->
                return PostgresError(PostgresCredentialErrors.fromManaged replacement.Owner.ResourceId error)
            | _ -> return invalidOp "Unexpected managed Secret rotation outcome."
        }
