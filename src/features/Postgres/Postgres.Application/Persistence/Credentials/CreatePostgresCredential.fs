namespace Pole.Features.Postgres.Application.Persistence.Credentials

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open MongoDB.Bson
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Persistence
open Pole.Features.Secrets.Contracts

type CreatePostgresCredential =
    delegate of
        tenantId : string *
        clusterId : string *
        username : string *
        password : string *
        cancellationToken : CancellationToken -> Task<PostgresOutcome<PreparedManagedSecret, PostgresCredentialError>>

type PostgresCredentialCreator(prepareSecret : PrepareManagedSecret) =
    member _.InvokeAsync(tenantId, clusterId, username, password, cancellationToken : CancellationToken) =
        task {
            let values = Dictionary<string, string>()
            values[PostgresManagedSecret.usernameKey] <- username
            values[PostgresManagedSecret.passwordKey] <- password
            let! prepared =
                prepareSecret.Invoke(
                    tenantId,
                    ObjectId.GenerateNewId().ToString(),
                    PostgresManagedSecret.owner clusterId,
                    PostgresManagedSecret.name clusterId,
                    1L,
                    values,
                    ManagedSecretKubernetesType.BasicAuthentication,
                    cancellationToken)
            match TypedResultInterop.toResult prepared with
            | Ok value -> return PostgresSuccess value
            | Error _ -> return PostgresError(PostgresCredentialProtectionUnavailable clusterId)
        }
