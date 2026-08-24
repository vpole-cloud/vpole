namespace Pole.Features.Search.Application.Persistence

open System.Text.RegularExpressions
open MongoDB.Bson
open MongoDB.Driver
open Pole.Features.Search.Application.Common
open Pole.Features.Search.Application.Projection
open Pole.Features.Search.Domain

[<RequireQualifiedAccess>]
module internal SearchProjectionFilters =
    let sourceByIdentity (sourceType : SearchDocumentType) (tenantId : string) (sourceId : string) =
        Builders<SearchSourceDocument>.Filter.And(
            Builders<SearchSourceDocument>.Filter.Eq(_.TenantId, tenantId),
            Builders<SearchSourceDocument>.Filter.Eq(_.SourceType, sourceType),
            Builders<SearchSourceDocument>.Filter.Eq(_.SourceId, sourceId))

    let documentsBySource (sourceType : SearchDocumentType) (tenantId : string) (sourceId : string) =
        Builders<SearchDocument>.Filter.And(
            Builders<SearchDocument>.Filter.Eq(_.TenantId, tenantId),
            Builders<SearchDocument>.Filter.Eq(_.SourceType, sourceType),
            Builders<SearchDocument>.Filter.Eq(_.SourceId, sourceId))

    let acceptsOperationalStatus (status : SearchOperationalStatusUpdate) (eventOccurredAtUtc : System.DateTime) =
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
                filter.Lte("OperationalStatus.EventOccurredAtUtc", eventOccurredAtUtc)))

    let sourceAcceptingOperationalStatus
        (sourceType : SearchDocumentType)
        (tenantId : string)
        (sourceId : string)
        (status : SearchOperationalStatusUpdate)
        (eventOccurredAtUtc : System.DateTime) =
        Builders<SearchSourceDocument>.Filter.And(
            sourceByIdentity sourceType tenantId sourceId,
            acceptsOperationalStatus status eventOccurredAtUtc)

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
                |> List.map (fun value -> BsonInt32(int value) :> BsonValue)
                |> bsonArray
            conditions <-
                conditions @
                [ bsonDocument [
                    BsonElement(
                        "SourceType",
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
