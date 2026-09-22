namespace Pole.Features.Search.Application.Common

open System.Runtime.CompilerServices
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing
open Pole.Features.Search.Application.Endpoints

[<AbstractClass; Sealed>]
type SearchEndpoints =
    [<Extension>]
    static member MapSearchEndpoints(endpoints : IEndpointRouteBuilder) =
        endpoints.MapGroup("/search")
            .WithTags("Search")
            .WithMetadata(Pole.Features.Core.Multitenancy.TenantEndpointMetadata())
            .MapGetSearchEndpoint()
        |> ignore
        endpoints

