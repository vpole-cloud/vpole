namespace Pole.Features.Postgres.Application.Common

open System.Text
open Pole.Features.Secrets.Contracts

type PostgresClusterCredentials =
    { Username : string
      Password : string }

    override _.ToString() = "PostgresClusterCredentials([REDACTED])"

type PostgresProjectedCredentials =
    | OperatorCredentials of
        username : string *
        projection : SecretProjectionReference
    | StatefulSetCredentials of projection : SecretProjectionReference

    member this.Projection =
        match this with
        | OperatorCredentials(_, projection) -> projection
        | StatefulSetCredentials projection -> projection

[<RequireQualifiedAccess>]
module PostgresManagedSecret =
    let owner clusterId = ManagedSecretOwner(PostgresResourceDeployment.ResourceType.Value, clusterId)
    let name clusterId = $"postgres-{clusterId}-credentials"
    let usernameKey = "username"
    let passwordKey = "password"

    let maximumValueBytes = 65536L
    let valueBytes value = if isNull value then 0L else int64 (Encoding.UTF8.GetByteCount(value : string))
    let totalValueBytes values = values |> Seq.sumBy valueBytes
