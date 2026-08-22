namespace Pole.Features.Postgres.Application.Persistence.Credentials

open System.Threading
open System.Threading.Tasks
open MongoDB.Driver
open Pole.Encryption
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Persistence
open Pole.Features.Postgres.Domain

type ResolvePostgresClusterCredentials =
    delegate of tenantId : string * clusterId : string * cancellationToken : CancellationToken -> Task<PostgresOutcome<PostgresClusterCredentials, PostgresCredentialError>>

type MongoResolvePostgresClusterCredentials(
    credentials : IMongoCollection<PostgresCredential>,
    encryption : IEnvelopeEncryptionService) =

    let decrypt (credential : PostgresCredential) fieldName encryptedValue cancellationToken =
        encryption.DecryptStringAsync(
            encryptedValue,
            PostgresCredentialEncryption.context
                credential.TenantId
                credential.PostgresClusterId
                fieldName
                credential.Version,
            cancellationToken).AsTask()

    member _.InvokeAsync(tenantId, clusterId, cancellationToken : CancellationToken) =
        task {
            PostgresPersistenceGuards.credentialIdentity tenantId clusterId
            let! credential =
                credentials
                    .Find(PostgresFilters.credentialByCluster tenantId clusterId)
                    .FirstOrDefaultAsync(cancellationToken)
            if isNull (box credential) then
                return PostgresError(PostgresCredentialNotFound clusterId)
            else
                let! plaintextUsername =
                    decrypt credential PostgresCredential.UsernameField credential.EncryptedUsername cancellationToken
                match plaintextUsername.TryGetValue() with
                | struct (false, _, _) ->
                    return PostgresError(PostgresCredentialProtectionUnavailable clusterId)
                | struct (true, username, _) ->
                    let! plaintextPassword =
                        decrypt credential PostgresCredential.PasswordField credential.EncryptedPassword cancellationToken
                    match plaintextPassword.TryGetValue() with
                    | struct (false, _, _) ->
                        return PostgresError(PostgresCredentialProtectionUnavailable clusterId)
                    | struct (true, password, _) ->
                        return PostgresSuccess { Username = username; Password = password }
        }
