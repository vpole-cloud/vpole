namespace Pole.Features.Postgres.Application.Persistence.Credentials

type PostgresCredentialError =
    | PostgresCredentialNotFound of clusterId : string
    | PostgresCredentialProtectionUnavailable of clusterId : string
    | PostgresCredentialRotationConflict of clusterId : string
