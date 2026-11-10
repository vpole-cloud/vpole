namespace Pole.Features.Search.Application.Mongo

open System.Text.RegularExpressions
open MongoDB.Bson
open MongoDB.Driver
open Pole.Features.Search.Application.Common
open Pole.Features.Search.Application.Projection
open Pole.Features.Search.Contracts
open Pole.Features.Search.Domain

[<RequireQualifiedAccess>]
module internal SearchProjectionFilters =
    let sourceByIdentity (identity: SearchProjectionIdentity) =
        Builders<SearchSourceDocument>.Filter
            .And(
                Builders<SearchSourceDocument>.Filter.Eq(_.TenantId, identity.TenantId),
                Builders<SearchSourceDocument>.Filter.Eq(_.ResourceTypeId, identity.ResourceTypeId.Value),
                Builders<SearchSourceDocument>.Filter.Eq(_.SourceId, identity.SourceId)
            )

    let documentsBySource (identity: SearchProjectionIdentity) =
        Builders<SearchDocument>.Filter
            .And(
                Builders<SearchDocument>.Filter.Eq(_.TenantId, identity.TenantId),
                Builders<SearchDocument>.Filter.Eq(_.ResourceTypeId, identity.ResourceTypeId.Value),
                Builders<SearchDocument>.Filter.Eq(_.SourceId, identity.SourceId)
            )

    let versionByIdentity (identity: SearchProjectionIdentity) =
        Builders<SearchProjectionVersionDocument>.Filter
            .And(
                Builders<SearchProjectionVersionDocument>.Filter.Eq(_.TenantId, identity.TenantId),
                Builders<SearchProjectionVersionDocument>.Filter.Eq(_.ResourceTypeId, identity.ResourceTypeId.Value),
                Builders<SearchProjectionVersionDocument>.Filter.Eq(_.SourceId, identity.SourceId)
            )

    let acceptsOperationalStatus (status: SearchOperationalStatusSnapshot) =
        let filter = Builders<SearchSourceDocument>.Filter

        filter.Or(
            filter.Exists((fun source -> source.OperationalStatus :> obj), false),
            filter.Eq(_.OperationalStatus, None),
            filter.Lt(_.OperationalStatus.Value.ResourceRevision, status.ResourceRevision),
            filter.And(
                filter.Eq(_.OperationalStatus.Value.ResourceRevision, status.ResourceRevision),
                filter.Lt(_.OperationalStatus.Value.ObservedGeneration, status.ObservedGeneration)
            ),
            filter.And(
                filter.Eq(_.OperationalStatus.Value.ResourceRevision, status.ResourceRevision),
                filter.Eq(_.OperationalStatus.Value.ObservedGeneration, status.ObservedGeneration),
                filter.Lte(_.OperationalStatus.Value.EventOccurredAtUtc, status.EventOccurredAtUtc)
            )
        )

    let sourceAcceptingOperationalStatus
        (identity: SearchProjectionIdentity)
        (status: SearchOperationalStatusSnapshot)
        =
        Builders<SearchSourceDocument>.Filter.And(sourceByIdentity identity, acceptsOperationalStatus status)

    let sourceMatch (query: SearchQuery) =
        let filter = Builders<SearchSourceDocument>.Filter

        let regex =
            BsonRegularExpression($"^{Regex.Escape(SearchDocumentFactory.normalize query.Query)}")

        let conditions =
            [ filter.ElemMatch(
                  (fun source -> source.SearchKeys :> seq<SearchSourceKey>),
                  Builders<SearchSourceKey>.Filter.Regex((fun key -> key.Value :> obj), regex)
              )
              filter.Eq(_.TenantId, query.TenantId) ]

        let conditions =
            match query.Types with
            | Some types when not (List.isEmpty types) ->
                filter.In(_.SourceApiName, types |> Seq.map _.ApiName) :: conditions
            | _ -> conditions

        filter.And(conditions)
