namespace Pole.Features.Postgres.Application.Persistence.Credentials

open System.Threading
open System.Threading.Tasks
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Persistence
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
            match TypedResultInterop.toResult result with
            | Ok values -> return PostgresSuccess values
            | Error error -> return PostgresError(PostgresCredentialErrors.fromManaged clusterId error)
        }
