namespace Pole.Features.Postgres.Application.Connections

[<RequireQualifiedAccess>]
module internal PostgresConnectionEndpoints =
    [<Literal>]
    let ReadWriteId = "postgres"

    [<Literal>]
    let TcpPort = 5432

    let buildReadWriteServiceName (resourceId : string) = $"pg-{resourceId}-rw"
