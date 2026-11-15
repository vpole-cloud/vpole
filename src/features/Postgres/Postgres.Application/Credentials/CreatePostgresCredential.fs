namespace Pole.Features.Postgres.Application.Credentials

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open MongoDB.Bson
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application
open Pole.Features.Secrets.Contracts

type CreatePostgresCredential =
    delegate of
        tenantId: string *
        clusterId: string *
        password: string *
        cancellationToken: CancellationToken ->
            Task<PostgresOutcome<PreparedManagedSecret, PostgresCredentialError>>

type PostgresCredentialCreator(prepareSecret: PrepareManagedSecret) =
    member _.InvokeAsync(tenantId, clusterId, password, cancellationToken: CancellationToken) =
        task {
            let values = Dictionary<string, string>()
            values[PostgresManagedSecret.usernameKey] <- PostgresManagedSecret.administrator
            values[PostgresManagedSecret.passwordKey] <- password

            let! prepared =
                prepareSecret.Invoke(
                    PrepareManagedSecretRequest(
                        tenantId,
                        ObjectId.GenerateNewId().ToString(),
                        PostgresManagedSecret.owner clusterId,
                        PostgresManagedSecret.name clusterId,
                        1L,
                        values,
                        ManagedSecretKubernetesType.BasicAuthentication,
                        ManagedSecretSharingPolicy.Shareable
                    ),
                    cancellationToken
                )

            match prepared.Value with
            | :? PreparedManagedSecret as value -> return PostgresSuccess value
            | :? ManagedSecretEncryptionUnavailable ->
                return PostgresError(PostgresCredentialProtectionUnavailable clusterId)
            | _ -> return invalidOp "Unexpected managed Secret preparation outcome."
        }
