namespace Pole.Features.Postgres.Application.Credentials

open System.Threading
open System.Threading.Tasks
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application

type ResolvePostgresClusterCredentials =
    delegate of
        tenantId: string * clusterId: string * cancellationToken: CancellationToken ->
            Task<PostgresOutcome<PostgresClusterCredentials, PostgresCredentialError>>

type ManagedResolvePostgresClusterCredentials(loadCredential: LoadPostgresCredential) =
    member _.InvokeAsync(tenantId, clusterId, cancellationToken: CancellationToken) =
        task {
            let! result = loadCredential.Invoke(tenantId, clusterId, cancellationToken)

            match result with
            | PostgresError error -> return PostgresError error
            | PostgresSuccess credential ->
                match
                    credential.Values.TryGetValue(PostgresManagedSecret.usernameKey),
                    credential.Values.TryGetValue(PostgresManagedSecret.passwordKey)
                with
                | (true, username), (true, password) ->
                    return
                        PostgresSuccess
                            { Username = username
                              Password = password }
                | _ -> return PostgresError(PostgresCredentialProtectionUnavailable clusterId)
        }
