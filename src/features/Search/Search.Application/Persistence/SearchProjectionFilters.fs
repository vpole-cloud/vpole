namespace Pole.Features.Search.Application.Persistence

open System.Text.RegularExpressions
open MongoDB.Bson
open MongoDB.Driver
open Pole.Features.Search.Application.Common
open Pole.Features.Search.Application.Projection
open Pole.Features.Search.Contracts
open Pole.Features.Search.Domain

[<RequireQualifiedAccess>]
module internal SearchProjectionFilters =
    let sourceByIdentity (identity : SearchProjectionIdentity) =
        Builders<SearchSourceDocument>.Filter.And(
            Builders<SearchSourceDocument>.Filter.Eq(_.TenantId, identity.TenantId),
            Builders<SearchSourceDocument>.Filter.Eq(_.ResourceTypeId, identity.ResourceTypeId.Value),
            Builders<SearchSourceDocument>.Filter.Eq(_.SourceId, identity.SourceId))

    let documentsBySource (identity : SearchProjectionIdentity) =
        Builders<SearchDocument>.Filter.And(
            Builders<SearchDocument>.Filter.Eq(_.TenantId, identity.TenantId),
            Builders<SearchDocument>.Filter.Eq(_.ResourceTypeId, identity.ResourceTypeId.Value),
            Builders<SearchDocument>.Filter.Eq(_.SourceId, identity.SourceId))

    let acceptsOperationalStatus (status : SearchOperationalStatusSnapshot) =
        let filter = Builders<SearchSourceDocument>.Filter
        filter.Or(
            filter.Exists("OperationalStatus", false),
            filter.Eq("OperationalStatus", BsonNull.Value),
            filter.Lt("OperationalStatus.DesiredGeneration", status.DesiredGeneration),
            filter.And(
                filter.Eq("OperationalStatus.DesiredGeneration", status.DesiredGeneration),
                filter.Lt("OperationalStatus.ObservedGeneration", status.ObservedGeneration)),
            filter.And(
                filter.Eq("OperationalStatus.DesiredGeneration", status.DesiredGeneration),
                filter.Eq("OperationalStatus.ObservedGeneration", status.ObservedGeneration),
                filter.Lte("OperationalStatus.EventOccurredAtUtc", status.EventOccurredAtUtc)))

    let sourceAcceptingOperationalStatus
        (identity : SearchProjectionIdentity)
        (status : SearchOperationalStatusSnapshot) =
        Builders<SearchSourceDocument>.Filter.And(
            sourceByIdentity identity,
            acceptsOperationalStatus status)

    let getSourceMatchStage (query : SearchQuery) =
        let bsonDocument (elements : BsonElement list) = BsonDocument(elements)
        let bsonArray (values : BsonValue list) = BsonArray(values)
        let regex = BsonRegularExpression($"^{Regex.Escape(SearchDocumentFactory.normalize query.Query)}")
        let mutable conditions =
            [ bsonDocument [
                BsonElement(
                    "SearchKeys.Value",
                    bsonDocument [ BsonElement("$regex", regex) ])
              ] :> BsonValue ]

        conditions <-
            conditions @
            [ bsonDocument [ BsonElement("TenantId", BsonString(query.TenantId)) ] :> BsonValue ]

        match query.Types with
        | Some types when not (List.isEmpty types) ->
            let typeValues =
                types
                |> List.map (fun value -> BsonString(value.ApiName) :> BsonValue)
                |> bsonArray
            conditions <-
                conditions @
                [ bsonDocument [
                    BsonElement(
                        "SourceApiName",
                        bsonDocument [ BsonElement("$in", typeValues) ])
                  ] :> BsonValue ]
        | _ -> ()

        if conditions.Length = 1 then
            bsonDocument [ BsonElement("$match", conditions.Head.AsBsonDocument) ]
        else
            bsonDocument [
                BsonElement(
                    "$match",
                    bsonDocument [ BsonElement("$and", bsonArray conditions) ])
            ]
