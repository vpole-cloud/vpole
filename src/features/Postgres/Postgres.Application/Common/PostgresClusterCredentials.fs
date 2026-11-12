namespace Pole.Features.Postgres.Application.Common

open System.Text
open Pole.Features.Secrets.Contracts

type PostgresClusterCredentials =
    { Username: string
      Password: string }

    override _.ToString() =
        "PostgresClusterCredentials([REDACTED])"

[<RequireQualifiedAccess>]
module PostgresManagedSecret =
    let owner clusterId =
        ManagedSecretOwner(PostgresResourceDeployment.ResourceType.Value, clusterId)

    let name clusterId = $"postgres-{clusterId}-credentials"
    [<Literal>]
    let administrator = "postgres"

    let usernameKey = "username"
    let passwordKey = "password"

    let maximumValueBytes = 65536L

    let valueBytes value =
        if isNull value then
            0L
        else
            int64 (Encoding.UTF8.GetByteCount(value: string))

    let totalValueBytes values = values |> Seq.sumBy valueBytes
