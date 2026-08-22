namespace Pole.Features.Postgres.Application.Common

open Pole.Encryption
open Pole.Features.Postgres.Domain

type PostgresClusterCredentials =
    { Username : string
      Password : string }

    override _.ToString() = "PostgresClusterCredentials([REDACTED])"

[<RequireQualifiedAccess>]
module PostgresCredentialEncryption =
    let context tenantId clusterId fieldName version =
        EnvelopeEncryptionContext.ForTenant(
            tenantId,
            PostgresCredential.ObjectType,
            clusterId,
            fieldName,
            version)
