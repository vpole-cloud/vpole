// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Search.Application.Common

open System
open FluentValidation
open Microsoft.AspNetCore.Mvc
open MongoDB.Bson.Serialization.Attributes
open Pole.Features.Request
open Pole.Features.Search.Contracts
open Pole.Features.Search.Domain
open Pole.Mediator

type SearchOperationalStatusDto =
    { Status: string
      FailureCode: string option }

type SearchHitDto =
    { ResourceTypeId: string
      SourceType: string
      SourceId: string
      Title: string
      ResourceGroupId: string option
      ResourceGroupName: string option
      OperationalStatus: SearchOperationalStatusDto option }

type SearchQuery =
    { Query: string
      TenantId: string
      PageNumber: int
      PageSize: int
      Types: SearchSourceType list option }

type SearchFacetCount =
    { [<BsonElement("value")>]
      Value: int64 }

type SearchFacetResult =
    { [<BsonElement("items")>]
      Items: SearchSourceDocument array
      [<BsonElement("totalCount")>]
      TotalCount: SearchFacetCount array }

type SearchMatchedFields =
    { [<BsonElement("MatchedKeys")>]
      MatchedKeys: SearchSourceKey array
      [<BsonElement("BestWeight")>]
      BestWeight: int }

type SearchDocumentSet =
    { SourceType: SearchSourceType
      TenantId: string
      SourceId: string
      Title: string
      ResourceGroupId: string option
      ResourceGroupName: string option
      ResourceRevision: int64
      Documents: SearchDocument list }

type GetSearchOutcome = SearchPage of PaginatedResponse<SearchHitDto>

type GetSearchRequest() =
    interface IMediatorRequest<GetSearchOutcome>

    interface Pole.Features.Core.Multitenancy.ITenantReadOperation with
        member this.TenantId = this.TenantId

    [<Pole.ASP.Helpers.FromTenantHeader>]
    member val TenantId = "" with get, set

    [<FromQuery>]
    member val Query = "" with get, set

    [<FromQuery>]
    member val Types: string array = [||] with get, set

    [<FromQuery>]
    member val PageNumber = 1 with get, set

    [<FromQuery>]
    member val PageSize = 10 with get, set

type GetSearchRequestValidator(sourceTypes: SearchSourceTypeRegistry) =
    inherit AbstractValidator<GetSearchRequest>()

    do
        base
            .RuleFor(_.Query)
            .NotEmpty()
            .Must(fun value -> not (String.IsNullOrWhiteSpace(value)))
            .WithMessage("Query is required.")
        |> ignore

        base
            .RuleFor(_.Types)
            .Must(fun values ->
                isNull values
                || values |> Array.forall (fun value -> sourceTypes.ResolveApiName(value).IsSome))
            .WithMessage("Types contains an invalid value.")
        |> ignore

[<RequireQualifiedAccess>]
module SearchQuery =
    [<CompiledName("FromRequest")>]
    let ofRequest (sourceTypes: SearchSourceTypeRegistry) (request: GetSearchRequest) =
        let parsedTypes =
            request.Types
            |> Array.toList
            |> List.choose (fun value -> sourceTypes.ResolveApiName(value))

        { Query = request.Query
          TenantId = request.TenantId
          PageNumber = request.PageNumber
          PageSize = request.PageSize
          Types = if List.isEmpty parsedTypes then None else Some parsedTypes }
