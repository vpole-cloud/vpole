namespace Pole.Features.Postgres.Application.Persistence.Credentials

open System
open System.Threading
open System.Threading.Tasks
open MongoDB.Bson
open Pole.Encryption
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Persistence
open Pole.Features.Postgres.Domain

type CreatePostgresCredential =
    delegate of tenantId : string * clusterId : string * username : string * password : string * cancellationToken : CancellationToken -> Task<PostgresOutcome<PostgresCredential, PostgresCredentialError>>

type PostgresCredentialCreator(
    encryption : IEnvelopeEncryptionService,
    timeProvider : TimeProvider) =

    let encrypt tenantId clusterId fieldName version value cancellationToken =
        encryption.EncryptStringAsync(
            value,
            PostgresCredentialEncryption.context tenantId clusterId fieldName version,
            cancellationToken).AsTask()

    member _.InvokeAsync(tenantId, clusterId, username, password, cancellationToken : CancellationToken) =
        task {
            PostgresPersistenceGuards.credentialIdentity tenantId clusterId
            if String.IsNullOrWhiteSpace username then
                invalidArg "username" "PostgreSQL credential username is required."
            if String.IsNullOrWhiteSpace password then
                invalidArg "password" "PostgreSQL credential password is required."

            let version = 1L
            let! protectedUsername =
                encrypt tenantId clusterId PostgresCredential.UsernameField version username cancellationToken
            match TypedResultInterop.toResult protectedUsername with
            | Error _ ->
                return PostgresError(PostgresCredentialProtectionUnavailable clusterId)
            | Ok encryptedUsername ->
                let! protectedPassword =
                    encrypt tenantId clusterId PostgresCredential.PasswordField version password cancellationToken
                match TypedResultInterop.toResult protectedPassword with
                | Error _ ->
                    return PostgresError(PostgresCredentialProtectionUnavailable clusterId)
                | Ok encryptedPassword ->
                    let credential =
                        PostgresCredential.Create(
                            tenantId,
                            clusterId,
                            encryptedUsername,
                            encryptedPassword,
                            { Id = ObjectId.GenerateNewId().ToString()
                              CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime })
                    return PostgresSuccess credential
        }
