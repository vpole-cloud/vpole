namespace Pole.Features.Postgres.Endpoints

open System.Runtime.CompilerServices
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing

[<AbstractClass; Sealed>]
type PostgresClusterEndpoints =
    [<Extension>]
    static member MapPostgresClusterEndpoints(endpoints: IEndpointRouteBuilder) =
        endpoints.MapGroup("/postgres")
            .WithDisplayName("Postgres Cluster Endpoints")
            .WithTags("postgres")
            .MapCreatePostgresClusterEndpoint()
            .MapGetPostgresClustersEndpoint()
            .MapGetByIdPostgresClusterEndpoint()
            .MapUpdatePostgresClusterEndpoint()
            .MapDeletePostgresClusterEndpoint()
            |> ignore
        endpoints
