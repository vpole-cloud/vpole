namespace Pole.Features.Search.Application.Endpoints

open System.Runtime.CompilerServices
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open Microsoft.AspNetCore.Routing
open Pole.ASP.Helpers
open Pole.Features.Request
open Pole.Features.Search.Application.Common
open Pole.Features.Search.Application.Queries
open Pole.Mediator

type GetSearchHandler(sourceTypes: SearchSourceTypeRegistry, searchIndexedDocuments: SearchIndexedDocuments) =
    interface IMediatorOutcomeHandler<GetSearchRequest, GetSearchOutcome> with
        member _.HandleAsync(request, cancellationToken) =
            task {
                let! response =
                    searchIndexedDocuments.Invoke(SearchQuery.ofRequest sourceTypes request, cancellationToken)

                return SearchPage response
            }

[<AbstractClass; Sealed>]
type GetSearchResults =
    [<Extension>]
    static member MapGetSearchEndpoint(endpoints: IEndpointRouteBuilder) =
        endpoints
            .MapMethod<GetSearchResults>("/", nameof GetSearchResults.Handle, EndpointHttpMethod.Get)
            .WithName("GetSearch")
            .WithSummary("Searches across all indexed entities.")
            .Produces<PaginatedResponse<SearchHitDto>>(StatusCodes.Status200OK)
        |> ignore

        endpoints

    static member Handle([<AsParameters>] request: GetSearchRequest, [<FromServices>] mediator: IMediator) =
        task {
            let! result = mediator.Send request

            return
                result.ToHttpResult(fun outcome ->
                    match outcome with
                    | SearchPage value -> Results.Ok(value))
        }
