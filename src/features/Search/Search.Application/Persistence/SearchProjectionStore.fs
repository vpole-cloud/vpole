namespace Pole.Features.Search.Application.Persistence

open System
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open MongoDB.Bson
open MongoDB.Bson.Serialization
open MongoDB.Driver
open Pole.Features.Request
open Pole.Features.Search.Application.Common
open Pole.Features.Search.Application.Projection
open Pole.Features.Search.Domain

type internal SearchProjectionStore(database : IMongoDatabase) =
    let documents = database.GetCollection<SearchDocument>("SearchDocuments")
    let sources = database.GetCollection<SearchSourceDocument>("SearchSourceDocuments")

    let bsonDocument (elements : BsonElement list) = BsonDocument(elements)
    let bsonArray (values : BsonValue list) = BsonArray(values)

    let toSearchHit (document : SearchSourceDocument) =
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

    let toOperationalStatus (status : SearchOperationalStatusUpdate) eventOccurredAtUtc =
        { DesiredGeneration = status.DesiredGeneration
          ObservedGeneration = status.ObservedGeneration
          Status = status.Status
          FailureCode = status.FailureCode
          EventOccurredAtUtc = eventOccurredAtUtc }

    let rec persistOperationalStatus
        sourceType
        tenantId
        sourceId
        (status : SearchOperationalStatusUpdate)
        eventOccurredAtUtc
        (cancellationToken : CancellationToken) =
        task {
            let identity = SearchProjectionFilters.sourceByIdentity sourceType tenantId sourceId
            let filter =
                SearchProjectionFilters.sourceAcceptingOperationalStatus
                    sourceType
                    tenantId
                    sourceId
                    status
                    eventOccurredAtUtc
            let operationalStatus = Some(toOperationalStatus status eventOccurredAtUtc)
            let update = Builders<SearchSourceDocument>.Update.Set(_.OperationalStatus, operationalStatus)
            let! updated = sources.UpdateOneAsync(filter, update, cancellationToken = cancellationToken)

            if updated.MatchedCount = 0L then
                let! exists = sources.Find(identity).Limit(1).AnyAsync(cancellationToken)
                if not exists then
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
                        do! sources.InsertOneAsync(pendingSource, cancellationToken = cancellationToken)
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

    member _.PersistDocumentSetAsync(documentSet : SearchDocumentSet, cancellationToken : CancellationToken) =
        task {
            let documentFilter =
                SearchProjectionFilters.documentsBySource
                    documentSet.SourceType
                    documentSet.TenantId
                    documentSet.SourceId
            do! documents.DeleteManyAsync(documentFilter, cancellationToken = cancellationToken) :> Task

            if not (List.isEmpty documentSet.Documents) then
                do! documents.InsertManyAsync(documentSet.Documents, cancellationToken = cancellationToken)

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
                    sources.UpdateOneAsync(
                        SearchProjectionFilters.sourceByIdentity
                            documentSet.SourceType
                            documentSet.TenantId
                            documentSet.SourceId,
                        update,
                        UpdateOptions(IsUpsert = true),
                        cancellationToken)
                return ()
        }

    member _.RemoveDocumentSetAsync(
        sourceType : SearchDocumentType,
        tenantId : string,
        sourceId : string,
        cancellationToken : CancellationToken) =
        task {
            do!
                documents.DeleteManyAsync(
                    SearchProjectionFilters.documentsBySource sourceType tenantId sourceId,
                    cancellationToken = cancellationToken)
                :> Task
            do!
                sources.DeleteManyAsync(
                    SearchProjectionFilters.sourceByIdentity sourceType tenantId sourceId,
                    cancellationToken = cancellationToken)
                :> Task
        }

    member _.PersistOperationalStatusAsync(
        sourceType : SearchDocumentType,
        tenantId : string,
        sourceId : string,
        status : SearchOperationalStatusUpdate,
        eventOccurredAtUtc : DateTime,
        cancellationToken : CancellationToken) =
        persistOperationalStatus sourceType tenantId sourceId status eventOccurredAtUtc cancellationToken

    member _.SearchAsync(query : SearchQuery, cancellationToken : CancellationToken) =
        task {
            let skip = (max query.PageNumber 1 - 1) * max query.PageSize 1
            let limit = max query.PageSize 1
            let regex = BsonRegularExpression($"^{Regex.Escape(SearchDocumentFactory.normalize query.Query)}")
            let pipeline =
                [|
                    SearchProjectionFilters.getSourceMatchStage query
                    bsonDocument [
                        BsonElement(
                            "$addFields",
                            bsonDocument [
                                BsonElement(
                                    "MatchedKeys",
                                    bsonDocument [
                                        BsonElement(
                                            "$filter",
                                            bsonDocument [
                                                BsonElement("input", "$SearchKeys")
                                                BsonElement("as", "key")
                                                BsonElement(
                                                    "cond",
                                                    bsonDocument [
                                                        BsonElement(
                                                            "$regexMatch",
                                                            bsonDocument [
                                                                BsonElement("input", "$$key.Value")
                                                                BsonElement("regex", regex) ]) ]) ]) ]) ]) ]
                    bsonDocument [
                        BsonElement(
                            "$addFields",
                            bsonDocument [
                                BsonElement(
                                    "BestWeight",
                                    bsonDocument [ BsonElement("$max", "$MatchedKeys.Weight") ]) ]) ]
                    bsonDocument [
                        BsonElement(
                            "$sort",
                            bsonDocument [
                                BsonElement("BestWeight", -1)
                                BsonElement("Title", -1)
                                BsonElement("SourceType", 1)
                                BsonElement("SourceId", 1) ]) ]
                    bsonDocument [
                        BsonElement(
                            "$project",
                            bsonDocument [
                                BsonElement("MatchedKeys", 0)
                                BsonElement("BestWeight", 0) ]) ]
                    bsonDocument [
                        BsonElement(
                            "$facet",
                            bsonDocument [
                                BsonElement(
                                    "items",
                                    bsonArray [
                                        bsonDocument [ BsonElement("$skip", skip) ]
                                        bsonDocument [ BsonElement("$limit", limit) ] ])
                                BsonElement(
                                    "totalCount",
                                    bsonArray [
                                        bsonDocument [ BsonElement("$count", "value") ] ]) ]) ]
                |]

            let! facet =
                sources
                    .Aggregate<BsonDocument>(
                        pipeline,
                        AggregateOptions(Comment = "Pole|Feature=Search|Operation=GetSearch"))
                    .FirstOrDefaultAsync(cancellationToken)

            let items =
                if isNull facet || not (facet.Contains("items")) then
                    [||]
                else
                    facet["items"].AsBsonArray
                    |> Seq.map (fun value ->
                        BsonSerializer.Deserialize<SearchSourceDocument>(value.AsBsonDocument))
                    |> Seq.toArray
            let totalCount =
                if isNull facet || not (facet.Contains("totalCount")) then
                    0L
                else
                    let counts = facet["totalCount"].AsBsonArray
                    if counts.Count = 0 then 0L else counts[0].AsBsonDocument["value"].ToInt64()

            return
                PaginatedResponse<SearchHitDto>(
                    items |> Array.map toSearchHit,
                    totalCount,
                    query.PageNumber,
                    query.PageSize)
        }
