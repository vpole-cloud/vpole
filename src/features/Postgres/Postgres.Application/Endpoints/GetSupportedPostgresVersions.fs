// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Postgres.Application.Endpoints

open System.Runtime.CompilerServices
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open Microsoft.AspNetCore.Routing
open Pole.ASP.Helpers
open Pole.Features.Postgres.Application.Common
open Pole.Mediator

type GetSupportedPostgresVersionsResponse =
    { From: int
      ``To``: int }

type GetSupportedPostgresVersionsOutcome = SupportedPostgresVersions of GetSupportedPostgresVersionsResponse

type GetSupportedPostgresVersionsRequest() =
    interface IMediatorRequest<GetSupportedPostgresVersionsOutcome>

type GetSupportedPostgresVersionsHandler(settings: PostgresSettings) =
    interface IMediatorOutcomeHandler<GetSupportedPostgresVersionsRequest, GetSupportedPostgresVersionsOutcome> with
        member _.HandleAsync(_, _) =
            task {
                return
                    { From = settings.SupportedVersions.From
                      ``To`` = settings.SupportedVersions.``To`` }
                    |> SupportedPostgresVersions
            }

[<AbstractClass; Sealed>]
type GetSupportedPostgresVersions =
    [<Extension>]
    static member MapGetSupportedPostgresVersionsEndpoint(endpoints: IEndpointRouteBuilder) =
        endpoints
            .MapMethod<GetSupportedPostgresVersions>(
                "/supported-versions",
                nameof GetSupportedPostgresVersions.Handle,
                EndpointHttpMethod.Get
            )
            .WithName("GetSupportedPostgresVersions")
        |> ignore

        endpoints

    static member Handle([<FromServices>] mediator: IMediator) =
        task {
            let! result = mediator.Send(GetSupportedPostgresVersionsRequest())

            return
                result.ToHttpResult(fun outcome ->
                    match outcome with
                    | SupportedPostgresVersions value -> Results.Ok(value))
        }
