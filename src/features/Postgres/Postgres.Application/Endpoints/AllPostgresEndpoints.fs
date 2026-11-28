// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Postgres.Application.Endpoints

open System.Runtime.CompilerServices
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing

[<AbstractClass; Sealed>]
type PostgresClusterEndpoints =
    [<Extension>]
    static member MapPostgresClusterEndpoints(endpoints: IEndpointRouteBuilder) =
        endpoints
            .MapGroup("/postgres")
            .WithDisplayName("Postgres Cluster Endpoints")
            .WithTags("postgres")
            .MapCreatePostgresClusterEndpoint()
            .MapGetSupportedPostgresVersionsEndpoint()
            .MapGetPostgresClustersEndpoint()
            .MapGetByIdPostgresClusterEndpoint()
            .MapUpdatePostgresClusterEndpoint()
            .MapUpdatePostgresClusterPasswordEndpoint()
            .MapGetPostgresClusterConnectionStringEndpoint()
            .MapDeletePostgresClusterEndpoint()
        |> ignore

        endpoints
