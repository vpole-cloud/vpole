namespace Pole.Features.Postgres.Application.Common

open System
open System.Threading
open System.Threading.Tasks
open MongoDB.Bson
open MongoDB.Driver
open Pole.Encryption
open Pole.Features.Postgres.Domain
open Pole.ResultType

type PostgresClusterCredentials =
    { Username : string
      Password : string }

    override _.ToString() = "PostgresClusterCredentials([REDACTED])"

type IPostgresCredentialService =
    abstract CreateAsync :
        tenantId : string *
        clusterId : string *
        username : string *
        password : string *
        cancellationToken : CancellationToken -> Task<Result<PostgresCredential>>

    abstract RotateAsync :
        credential : PostgresCredential *
        password : string *
        cancellationToken : CancellationToken -> Task<Result<PostgresCredential>>

    abstract ResolveAsync :
        tenantId : string *
        clusterId : string *
        cancellationToken : CancellationToken -> Task<Result<PostgresClusterCredentials>>

[<RequireQualifiedAccess>]
module PostgresCredentials =
    let collection (dbContext : IPostgresDbContext) =
        dbContext.Database.GetCollection<PostgresCredential>(PostgresCredential.CollectionName)

    let encryptionContext tenantId clusterId fieldName version =
        EnvelopeEncryptionContext.ForTenant(
            tenantId,
            PostgresCredential.ObjectType,
            clusterId,
            fieldName,
            version)

type PostgresCredentialService(
    dbContext : IPostgresDbContext,
    envelopeEncryption : IEnvelopeEncryptionService,
    timeProvider : TimeProvider) =

    let encrypt tenantId clusterId fieldName version value cancellationToken =
        envelopeEncryption.EncryptStringAsync(
            value,
            PostgresCredentials.encryptionContext tenantId clusterId fieldName version,
            cancellationToken)

    let decrypt (credential : PostgresCredential) fieldName encryptedValue cancellationToken =
        envelopeEncryption.DecryptStringAsync(
            encryptedValue,
            PostgresCredentials.encryptionContext
                credential.TenantId
                credential.PostgresClusterId
                fieldName
                credential.Version,
            cancellationToken)

    interface IPostgresCredentialService with
        member _.CreateAsync(tenantId, clusterId, username, password, cancellationToken) =
            task {
                let credentialId = ObjectId.GenerateNewId().ToString()
                let version = 1L
                let! encryptedUsername =
                    encrypt tenantId clusterId PostgresCredential.UsernameField version username cancellationToken

                match encryptedUsername.TryGetValue() with
                | struct (false, _, failure) ->
                    return failure |> ResultF.ofFailure
                | struct (true, protectedUsername, _) ->
                    let! encryptedPassword =
                        encrypt tenantId clusterId PostgresCredential.PasswordField version password cancellationToken

                    match encryptedPassword.TryGetValue() with
                    | struct (false, _, failure) ->
                        return failure |> ResultF.ofFailure
                    | struct (true, protectedPassword, _) ->
                        let nowUtc = timeProvider.GetUtcNow().UtcDateTime
                        return
                            PostgresCredential.Create(
                                tenantId,
                                clusterId,
                                protectedUsername,
                                protectedPassword,
                                { Id = credentialId
                                  CreatedAtUtc = nowUtc })
                            |> ResultF.ofValue
            }

        member _.RotateAsync(credential, password, cancellationToken) =
            task {
                let! username =
                    decrypt credential PostgresCredential.UsernameField credential.EncryptedUsername cancellationToken

                match username.TryGetValue() with
                | struct (false, _, failure) ->
                    return failure |> ResultF.ofFailure
                | struct (true, plaintextUsername, _) ->
                    let nextVersion =
                        if credential.Version = Int64.MaxValue then
                            raise (OverflowException("PostgreSQL credential version overflowed."))
                        else
                            credential.Version + 1L

                    let! encryptedUsername =
                        encrypt
                            credential.TenantId
                            credential.PostgresClusterId
                            PostgresCredential.UsernameField
                            nextVersion
                            plaintextUsername
                            cancellationToken

                    match encryptedUsername.TryGetValue() with
                    | struct (false, _, failure) ->
                        return failure |> ResultF.ofFailure
                    | struct (true, protectedUsername, _) ->
                        let! encryptedPassword =
                            encrypt
                                credential.TenantId
                                credential.PostgresClusterId
                                PostgresCredential.PasswordField
                                nextVersion
                                password
                                cancellationToken

                        match encryptedPassword.TryGetValue() with
                        | struct (false, _, failure) ->
                            return failure |> ResultF.ofFailure
                        | struct (true, protectedPassword, _) ->
                            return
                                credential.Rotate(
                                    protectedUsername,
                                    protectedPassword,
                                    timeProvider.GetUtcNow().UtcDateTime)
                                |> ResultF.ofValue
            }

        member _.ResolveAsync(tenantId, clusterId, cancellationToken) =
            task {
                let! credential =
                    (PostgresCredentials.collection dbContext)
                        .Find(fun item ->
                            item.TenantId = tenantId &&
                            item.PostgresClusterId = clusterId)
                        .FirstOrDefaultAsync(cancellationToken)

                if isNull (box credential) then
                    return
                        Failures.NotFound(
                            "PostgreSQL credentials were not found.",
                            clusterId)
                        |> ResultF.ofFailure
                else
                    let! username =
                        decrypt credential PostgresCredential.UsernameField credential.EncryptedUsername cancellationToken

                    match username.TryGetValue() with
                    | struct (false, _, failure) ->
                        return failure |> ResultF.ofFailure
                    | struct (true, plaintextUsername, _) ->
                        let! password =
                            decrypt credential PostgresCredential.PasswordField credential.EncryptedPassword cancellationToken

                        match password.TryGetValue() with
                        | struct (false, _, failure) ->
                            return failure |> ResultF.ofFailure
                        | struct (true, plaintextPassword, _) ->
                            return
                                { Username = plaintextUsername
                                  Password = plaintextPassword }
                                |> ResultF.ofValue
            }
