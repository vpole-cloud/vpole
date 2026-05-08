namespace Pole.Features.Search.Common

open System.Runtime.CompilerServices
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing
open Pole.Features.Search.Endpoints

[<AbstractClass; Sealed>]
type SearchEndpoints =
    [<Extension>]
    static member MapSearchEndpoints(endpoints : IEndpointRouteBuilder) =
        endpoints.MapGroup("/search")
            .WithTags("Search")
            .MapGetSearchEndpoint()
        |> ignore
        endpoints
