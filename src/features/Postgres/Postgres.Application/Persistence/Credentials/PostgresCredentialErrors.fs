namespace Pole.Features.Postgres.Application.Persistence.Credentials

open Pole.Features.Secrets.Contracts

type PostgresCredentialError =
    | PostgresCredentialNotFound of clusterId : string
    | PostgresCredentialProtectionUnavailable of clusterId : string
    | PostgresCredentialRotationConflict of clusterId : string
    | PostgresCredentialReferenceRejected of clusterId : string
    | PostgresCredentialValuesTooLarge of clusterId : string

[<RequireQualifiedAccess>]
module PostgresCredentialErrors =
    let fromManaged clusterId (error : ManagedSecretFailure) =
        match error with
        | :? ManagedSecretUnavailable -> PostgresCredentialNotFound clusterId
        | :? ManagedSecretChanged -> PostgresCredentialRotationConflict clusterId
        | :? ManagedSecretReferenceRejected -> PostgresCredentialReferenceRejected clusterId
        | _ -> PostgresCredentialProtectionUnavailable clusterId
