namespace Pole.Features.Search.Common

open System
open FluentValidation
open Microsoft.AspNetCore.Mvc
open Pole.Features.Search.Domain
open Pole.Mediator

type SearchHitDto =
    { SourceType : string
      SourceId : string
      Title : string
      ResourceGroupId : string option
      ResourceGroupName : string option }

type SearchQuery =
    { Query : string
      TenantId : string option
      PageNumber : int
      PageSize : int
      Types : SearchDocumentType list option }

type SearchAggregateResult =
    { Items : SearchDocument array
      TotalCount : int64 }

type SearchDocumentSet =
    { SourceType : SearchDocumentType
      TenantId : string
      SourceId : string
      Documents : SearchDocument list }

type GetSearchRequest() =
    interface IMediatorRequest<Pole.Features.Request.PaginatedResponse<SearchHitDto>>
    interface Pole.Features.Core.Multitenancy.ITenantReadOperation with
        member this.TenantId = this.TenantId
    [<FromHeader(Name = Pole.Features.Core.Multitenancy.TenantHeaders.TenantId)>]
    member val TenantId : string = null with get, set
    [<FromQuery>]
    member val Query = "" with get, set
    [<FromQuery>]
    member val Types : string array = [||] with get, set
    [<FromQuery>]
    member val PageNumber = 1 with get, set
    [<FromQuery>]
    member val PageSize = 10 with get, set

type GetSearchRequestValidator() =
    inherit AbstractValidator<GetSearchRequest>()
    do
        base.RuleFor(_.Query)
            .NotEmpty()
            .Must(fun value -> not (String.IsNullOrWhiteSpace(value)))
            .WithMessage("Query is required.")
        |> ignore

        base.RuleFor(_.Types)
            .Must(fun values ->
                isNull values
                || values
                   |> Array.forall (fun value ->
                       let mutable parsed = Unchecked.defaultof<SearchDocumentType>
                       Enum.TryParse<SearchDocumentType>(value, true, &parsed)))
            .WithMessage("Types contains an invalid value.")
        |> ignore

[<RequireQualifiedAccess>]
module SearchQuery =
    [<CompiledName("FromRequest")>]
    let ofRequest tenantId (request : GetSearchRequest) =
        let parsedTypes =
            request.Types
            |> Array.toList
            |> List.choose (fun value ->
                let mutable parsed = Unchecked.defaultof<SearchDocumentType>
                if Enum.TryParse<SearchDocumentType>(value, true, &parsed) then
                    Some parsed
                else
                    None)

        { Query = request.Query
          TenantId = tenantId
          PageNumber = request.PageNumber
          PageSize = request.PageSize
          Types = if List.isEmpty parsedTypes then None else Some parsedTypes }

    [<CompiledName("FromRequest")>]
    let ofRequestWithoutTenant (request : GetSearchRequest) =
        ofRequest None request
