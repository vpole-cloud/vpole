namespace Pole.Features.Postgres.Application.Credentials

open System.Threading
open System.Threading.Tasks
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application
open Pole.Features.Postgres.Application.Mongo
open Pole.Features.Secrets.Contracts

type LoadPostgresCredential =
    delegate of
        tenantId : string *
        clusterId : string *
        cancellationToken : CancellationToken -> Task<PostgresOutcome<ManagedSecretValues, PostgresCredentialError>>

type ManagedLoadPostgresCredential(readSecret : ReadManagedSecret) =
    member _.InvokeAsync(tenantId, clusterId, cancellationToken : CancellationToken) =
        task {
            let! result = readSecret.Invoke(tenantId, PostgresManagedSecret.owner clusterId, cancellationToken)
            match result.Value with
            | :? ManagedSecretValues as values -> return PostgresSuccess values
            | :? ManagedSecretFailure as error ->
                return PostgresError(PostgresCredentialErrors.fromManaged clusterId error)
            | _ -> return invalidOp "Unexpected managed Secret read outcome."
        }
