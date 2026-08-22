namespace Pole.Features.Postgres.Application.Persistence.Credentials

open System
open System.Threading
open System.Threading.Tasks
open Pole.Encryption
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Persistence
open Pole.Features.Postgres.Domain

type RotatePostgresCredential =
    delegate of credential : PostgresCredential * password : string * cancellationToken : CancellationToken -> Task<PostgresOutcome<PostgresCredential, PostgresCredentialError>>

type PostgresCredentialRotator(
    encryption : IEnvelopeEncryptionService,
    timeProvider : TimeProvider) =

    let decrypt (credential : PostgresCredential) fieldName encryptedValue cancellationToken =
        encryption.DecryptStringAsync(
            encryptedValue,
            PostgresCredentialEncryption.context
                credential.TenantId
                credential.PostgresClusterId
                fieldName
                credential.Version,
            cancellationToken).AsTask()

    let encrypt tenantId clusterId fieldName version value cancellationToken =
        encryption.EncryptStringAsync(
            value,
            PostgresCredentialEncryption.context tenantId clusterId fieldName version,
            cancellationToken).AsTask()

    member _.InvokeAsync(credential, password, cancellationToken : CancellationToken) =
        task {
            PostgresPersistenceGuards.credential credential
            if String.IsNullOrWhiteSpace password then
                invalidArg "password" "PostgreSQL credential password is required."

            let! plaintextUsername =
                decrypt credential PostgresCredential.UsernameField credential.EncryptedUsername cancellationToken
            match plaintextUsername.TryGetValue() with
            | struct (false, _, _) ->
                return PostgresError(PostgresCredentialProtectionUnavailable credential.PostgresClusterId)
            | struct (true, username, _) ->
                let nextVersion =
                    if credential.Version = Int64.MaxValue then
                        raise (OverflowException("PostgreSQL credential version overflowed."))
                    else
                        credential.Version + 1L
                let! protectedUsername =
                    encrypt
                        credential.TenantId
                        credential.PostgresClusterId
                        PostgresCredential.UsernameField
                        nextVersion
                        username
                        cancellationToken
                match protectedUsername.TryGetValue() with
                | struct (false, _, _) ->
                    return PostgresError(PostgresCredentialProtectionUnavailable credential.PostgresClusterId)
                | struct (true, encryptedUsername, _) ->
                    let! protectedPassword =
                        encrypt
                            credential.TenantId
                            credential.PostgresClusterId
                            PostgresCredential.PasswordField
                            nextVersion
                            password
                            cancellationToken
                    match protectedPassword.TryGetValue() with
                    | struct (false, _, _) ->
                        return PostgresError(PostgresCredentialProtectionUnavailable credential.PostgresClusterId)
                    | struct (true, encryptedPassword, _) ->
                        return
                            credential.Rotate(
                                encryptedUsername,
                                encryptedPassword,
                                timeProvider.GetUtcNow().UtcDateTime)
                            |> PostgresSuccess
        }
