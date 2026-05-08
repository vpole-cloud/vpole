namespace Pole.Features.Search.Endpoints

open System.Runtime.CompilerServices
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open Microsoft.AspNetCore.Routing
open Pole.ASP.Helpers
open Pole.Features.Request
open Pole.Features.Search.Common
open Pole.Features.Search.Services
open Pole.Mediator
open Pole.ResultType

type GetSearchHandler(searchService : ISearchService) =
    interface IMediatorHandler<GetSearchRequest, PaginatedResponse<SearchHitDto>> with
        member _.HandleAsync(request, cancellationToken) =
            task {
                let response = SearchQuery.ofRequest request
                let! result = searchService.Get response cancellationToken
                return Result<PaginatedResponse<SearchHitDto>>.op_Implicit(result)
            }

[<AbstractClass; Sealed>]
type SearchGetEndpoint =
    [<Extension>]
    static member MapGetSearchEndpoint(endpoints : IEndpointRouteBuilder) =
        endpoints.MapMethod<SearchGetEndpoint>("/", nameof SearchGetEndpoint.Handle, EndpointHttpMethod.Get)
            .WithName("GetSearch")
            .WithSummary("Searches across all indexed entities.")
            .Produces<PaginatedResponse<SearchHitDto>>(StatusCodes.Status200OK)
        |> ignore
        endpoints

    static member Handle([<AsParameters>] request : GetSearchRequest, [<FromServices>] mediator : IMediator) =
        task {
            let! result = mediator.Send request
            return result.ToHttpResult(fun value -> Results.Ok(value))
        }
