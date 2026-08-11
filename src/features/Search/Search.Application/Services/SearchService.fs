namespace Pole.Features.Search.Application.Services

open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open MongoDB.Bson
open MongoDB.Bson.Serialization
open MongoDB.Driver
open Pole.Features.Request
open Pole.Features.Search.Application.Common
open Pole.Features.Search.Domain
open Pole.Features.Search.Application.Projection
open Pole.Features.ResourceDeployments.Contracts

type SearchOperationalStatusUpdate =
    { DesiredGeneration : int64
      ObservedGeneration : int64
      Status : string
      FailureCode : string option }

type ISearchService =
    abstract member Create :
        documentSet : SearchDocumentSet ->
        cancellationToken : CancellationToken ->
            Task
    abstract member Get :
        query : SearchQuery ->
        cancellationToken : CancellationToken ->
            Task<PaginatedResponse<SearchHitDto>>
    abstract member Update :
        documentSet : SearchDocumentSet ->
        cancellationToken : CancellationToken ->
            Task
    abstract member Delete :
        sourceType : SearchDocumentType *
        tenantId : string *
        sourceId : string ->
            cancellationToken : CancellationToken ->
                Task
    abstract member UpdateOperationalStatus :
        sourceType : SearchDocumentType *
        tenantId : string *
        sourceId : string *
        status : SearchOperationalStatusUpdate *
        eventOccurredAtUtc : System.DateTime ->
            cancellationToken : CancellationToken ->
                Task

type SearchService(database : IMongoDatabase) =
    let collection = database.GetCollection<SearchDocument>("SearchDocuments")
    let sourceCollection = database.GetCollection<SearchSourceDocument>("SearchSourceDocuments")

    let bsonDoc (elements : BsonElement list) =
        BsonDocument(elements)

    let bsonArray (values : BsonValue list) =
        BsonArray(values)

    let sourceFilter (sourceType : SearchDocumentType) (tenantId : string) (sourceId : string) =
        Builders<SearchSourceDocument>.Filter.And(
            Builders<SearchSourceDocument>.Filter.Eq(_.TenantId, tenantId),
            Builders<SearchSourceDocument>.Filter.Eq(_.SourceType, sourceType),
            Builders<SearchSourceDocument>.Filter.Eq(_.SourceId, sourceId)
        )

    let rowFilter (sourceType : SearchDocumentType) (tenantId : string) (sourceId : string) =
        Builders<SearchDocument>.Filter.And(
            Builders<SearchDocument>.Filter.Eq(_.TenantId, tenantId),
            Builders<SearchDocument>.Filter.Eq(_.SourceType, sourceType),
            Builders<SearchDocument>.Filter.Eq(_.SourceId, sourceId)
        )

    let deleteBySource (sourceType : SearchDocumentType) (tenantId : string) (sourceId : string) (cancellationToken : CancellationToken) =
        task {
            do! collection.DeleteManyAsync(rowFilter sourceType tenantId sourceId, cancellationToken = cancellationToken) :> Task
            do! sourceCollection.DeleteManyAsync(sourceFilter sourceType tenantId sourceId, cancellationToken = cancellationToken) :> Task
        }

    let replaceSet (documentSet : SearchDocumentSet) (cancellationToken : CancellationToken) =
        task {
            do! collection.DeleteManyAsync(rowFilter documentSet.SourceType documentSet.TenantId documentSet.SourceId, cancellationToken = cancellationToken) :> Task

            if not (List.isEmpty documentSet.Documents) then
                do! collection.InsertManyAsync(documentSet.Documents, cancellationToken = cancellationToken)

                let candidate = SearchDocumentFactory.toSourceDocument documentSet
                let update =
                    Builders<SearchSourceDocument>.Update
                        .Set(_.Title, candidate.Title)
                        .Set(_.ResourceGroupId, candidate.ResourceGroupId)
                        .Set(_.ResourceGroupName, candidate.ResourceGroupName)
                        .Set(_.SearchKeys, candidate.SearchKeys)
                        .SetOnInsert(_.Id, candidate.Id)
                        .SetOnInsert(_.SourceType, candidate.SourceType)
                        .SetOnInsert(_.TenantId, candidate.TenantId)
                        .SetOnInsert(_.SourceId, candidate.SourceId)
                        .SetOnInsert(_.OperationalStatus, None)

                let! _ =
                    sourceCollection.UpdateOneAsync(
                        sourceFilter documentSet.SourceType documentSet.TenantId documentSet.SourceId,
                        update,
                        UpdateOptions(IsUpsert = true),
                        cancellationToken)

                return ()
        }

    let toSourceHitDto (document : SearchSourceDocument) =
        let operationalStatus =
            document.OperationalStatus
            |> Option.map (fun value ->
                { Status = value.Status
                  FailureCode = value.FailureCode })

        { SourceType = document.SourceType.ToString()
          SourceId = document.SourceId
          Title = document.Title
          ResourceGroupId = document.ResourceGroupId
          ResourceGroupName = document.ResourceGroupName
          OperationalStatus = operationalStatus }

    let toSearchOperationalStatus
        (status : SearchOperationalStatusUpdate)
        (eventOccurredAtUtc : System.DateTime) =
        { DesiredGeneration = status.DesiredGeneration
          ObservedGeneration = status.ObservedGeneration
          Status = status.Status
          FailureCode = status.FailureCode
          EventOccurredAtUtc = eventOccurredAtUtc }

    let placementFence (status : SearchOperationalStatusUpdate) (eventOccurredAtUtc : System.DateTime) =
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

    let rec persistOperationalStatus
        (sourceType : SearchDocumentType)
        (tenantId : string)
        (sourceId : string)
        (status : SearchOperationalStatusUpdate)
        (eventOccurredAtUtc : System.DateTime)
        (cancellationToken : CancellationToken) =
        task {
            let identity = sourceFilter sourceType tenantId sourceId
            let filter =
                Builders<SearchSourceDocument>.Filter.And(
                    identity,
                    placementFence status eventOccurredAtUtc)
            let operationalStatus =
                Some(toSearchOperationalStatus status eventOccurredAtUtc)
            let update =
                Builders<SearchSourceDocument>.Update.Set(
                    _.OperationalStatus,
                    operationalStatus)
            let! updated =
                sourceCollection.UpdateOneAsync(
                    filter,
                    update,
                    cancellationToken = cancellationToken)

            if updated.MatchedCount = 0L then
                let! existing =
                    sourceCollection
                        .Find(identity)
                        .Limit(1)
                        .AnyAsync(cancellationToken)

                if not existing then
                    let pendingSource =
                        { Id = SearchSourceDocument.createId()
                          SourceType = sourceType
                          TenantId = tenantId
                          SourceId = sourceId
                          Title = ""
                          ResourceGroupId = None
                          ResourceGroupName = None
                          SearchKeys = [||]
                          OperationalStatus = operationalStatus }
                    try
                        do! sourceCollection.InsertOneAsync(
                            pendingSource,
                            cancellationToken = cancellationToken)
                    with
                    | :? MongoWriteException as mongoException
                        when mongoException.WriteError.Category = ServerErrorCategory.DuplicateKey ->
                        return!
                            persistOperationalStatus
                                sourceType
                                tenantId
                                sourceId
                                status
                                eventOccurredAtUtc
                                cancellationToken
        }

    let escapePrefix (query : string) =
        $"^{Regex.Escape(query)}"

    let getSourceMatchStage (query : SearchQuery) =
        let normalizedQuery = SearchDocumentFactory.normalize query.Query
        let regex = BsonRegularExpression(escapePrefix normalizedQuery)

        let mutable conditions =
            [ bsonDoc [
                BsonElement(
                    "SearchKeys.Value",
                    bsonDoc [ BsonElement("$regex", regex) ])
              ] :> BsonValue ]

        conditions <-
            conditions @
            [ bsonDoc [ BsonElement("TenantId", BsonString(query.TenantId)) ] :> BsonValue ]

        match query.Types with
        | Some types when not (List.isEmpty types) ->
            let typeValues =
                types
                |> List.map (fun t -> BsonInt32(int t) :> BsonValue)
                |> bsonArray

            conditions <-
                conditions @
                [ bsonDoc [
                    BsonElement(
                        "SourceType",
                        bsonDoc [ BsonElement("$in", typeValues) ])
                  ] :> BsonValue ]
        | _ -> ()

        if conditions.Length = 1 then
            bsonDoc [ BsonElement("$match", conditions.Head.AsBsonDocument) ]
        else
            bsonDoc [
                BsonElement(
                    "$match",
                    bsonDoc [ BsonElement("$and", bsonArray conditions) ])
            ]

    interface ISearchService with
        member _.Create documentSet cancellationToken =
            replaceSet documentSet cancellationToken

        member _.Update documentSet cancellationToken =
            replaceSet documentSet cancellationToken

        member _.Delete(sourceType, tenantId, sourceId) cancellationToken =
            deleteBySource sourceType tenantId sourceId cancellationToken

        member _.UpdateOperationalStatus(sourceType, tenantId, sourceId, status, eventOccurredAtUtc) cancellationToken =
            persistOperationalStatus
                sourceType
                tenantId
                sourceId
                status
                eventOccurredAtUtc
                cancellationToken

        member _.Get query cancellationToken =
            task {
                let skip = (max query.PageNumber 1 - 1) * max query.PageSize 1
                let limit = max query.PageSize 1
                let normalizedQuery = SearchDocumentFactory.normalize query.Query
                let regex = BsonRegularExpression(escapePrefix normalizedQuery)

                // Query the source-level projection directly so one Mongo document equals one search hit.
                let pipeline =
                    [|
                        getSourceMatchStage query
                        // Keep only the keys that match the current prefix query.
                        bsonDoc [
                            BsonElement(
                                "$addFields",
                                bsonDoc [
                                    BsonElement(
                                        "MatchedKeys",
                                        bsonDoc [
                                            BsonElement(
                                                "$filter",
                                                bsonDoc [
                                                    BsonElement("input", "$SearchKeys")
                                                    BsonElement("as", "key")
                                                    BsonElement(
                                                        "cond",
                                                        bsonDoc [
                                                            BsonElement(
                                                                "$regexMatch",
                                                                bsonDoc [
                                                                    BsonElement("input", "$$key.Value")
                                                                    BsonElement("regex", regex)
                                                                ])
                                                        ])
                                                ])
                                        ])
                                ])
                        ]
                        // Rank each source document by the strongest matching key.
                        bsonDoc [
                            BsonElement(
                                "$addFields",
                                bsonDoc [
                                    BsonElement(
                                        "BestWeight",
                                        bsonDoc [ BsonElement("$max", "$MatchedKeys.Weight") ])
                                ])
                        ]
                        // Preserve the existing search ordering after the best weight is known.
                        bsonDoc [
                            BsonElement(
                                "$sort",
                                bsonDoc [
                                    BsonElement("BestWeight", -1)
                                    BsonElement("Title", -1)
                                    BsonElement("SourceType", 1)
                                    BsonElement("SourceId", 1)
                                ])
                        ]
                        // Remove temporary fields before deserializing back into SearchSourceDocument.
                        bsonDoc [
                            BsonElement(
                                "$project",
                                bsonDoc [
                                    BsonElement("MatchedKeys", 0)
                                    BsonElement("BestWeight", 0)
                                ])
                        ]
                        // Return the requested page and the total count in one round-trip.
                        bsonDoc [
                            BsonElement(
                                "$facet",
                                bsonDoc [
                                    BsonElement(
                                        "items",
                                        bsonArray [
                                            bsonDoc [ BsonElement("$skip", skip) ]
                                            bsonDoc [ BsonElement("$limit", limit) ]
                                        ])
                                    BsonElement(
                                        "totalCount",
                                        bsonArray [
                                            bsonDoc [ BsonElement("$count", "value") ]
                                        ])
                                ])
                        ]
                    |]

                let! facet =
                    sourceCollection
                        .Aggregate<BsonDocument>(
                            pipeline,
                            AggregateOptions(Comment = "Pole|Feature=Search|Operation=GetSearch"))
                        .FirstOrDefaultAsync(cancellationToken)

                let items =
                    if isNull facet || not (facet.Contains("items")) then
                        [||]
                    else
                        facet["items"].AsBsonArray
                        |> Seq.map (fun bson ->
                            BsonSerializer.Deserialize<SearchSourceDocument>(bson.AsBsonDocument))
                        |> Seq.toArray

                let totalCount =
                    if isNull facet || not (facet.Contains("totalCount")) then
                        0L
                    else
                        let countArray = facet["totalCount"].AsBsonArray
                        if countArray.Count = 0 then 0L else countArray[0].AsBsonDocument["value"].ToInt64()

                return
                    PaginatedResponse<SearchHitDto>(
                        items |> Array.map toSourceHitDto,
                        totalCount,
                        query.PageNumber,
                        query.PageSize)
            }

[<AbstractClass; Sealed>]
type SearchServiceRegistration =
    [<System.Runtime.CompilerServices.Extension>]
    static member AddSearchFeature(services : IServiceCollection) =
        services.AddScoped<ISearchService, SearchService>() |> ignore
        services
