namespace Pole.Features.Search.Application.Persistence

open System
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open MongoDB.Bson
open MongoDB.Bson.Serialization
open MongoDB.Driver
open Pole.Features.Events.Abstractions
open Pole.Features.Outbox.Contracts
open Pole.Features.Request
open Pole.Features.Search.Application.Common
open Pole.Features.Search.Application.Projection
open Pole.Features.Search.Contracts
open Pole.Features.Search.Domain
open Pole.Features.Search.Events

type internal SearchProjectionStore(
    database: IMongoDatabase,
    documents: IMongoCollection<SearchDocument>,
    sources: IMongoCollection<SearchSourceDocument>,
    versions: IMongoCollection<SearchProjectionVersionDocument>,
    outbox: StoreOutboxWrites
) =

    let bsonDocument (elements: BsonElement list) = BsonDocument(elements)
    let bsonArray (values: BsonValue list) = BsonArray(values)

    let storeChanged session (identity: SearchProjectionIdentity) revision generation ct =
        let message =
            SearchProjectionChangedEvent(
                identity.SourceId,
                identity.ResourceTypeId.Value,
                revision,
                generation,
                EventCore = EventCore.Create(identity.TenantId)
            )

        outbox.Invoke(session, [| OutboxWrite.Create(message) |], ct)

    let toSearchHit (document: SearchSourceDocument) =
        let operationalStatus =
            document.OperationalStatus
            |> Option.map (fun value ->
                { Status = value.Status
                  FailureCode = value.FailureCode })

        { ResourceTypeId = document.ResourceTypeId
          SourceType = document.SourceApiName
          SourceId = document.SourceId
          Title = document.Title
          ResourceGroupId = document.ResourceGroupId
          ResourceGroupName = document.ResourceGroupName
          OperationalStatus = operationalStatus }

    let toOperationalStatus (status: SearchOperationalStatusSnapshot) =
        { ResourceRevision = status.ResourceRevision
          ObservedGeneration = status.ObservedGeneration
          Status = status.Status
          FailureCode = Option.ofObj status.FailureCode
          EventOccurredAtUtc = status.EventOccurredAtUtc }

    let persistOperationalStatus
        (session: IClientSessionHandle)
        (sourceType: SearchSourceType)
        (identity: SearchProjectionIdentity)
        (status: SearchOperationalStatusSnapshot)
        (cancellationToken: CancellationToken)
        =
        task {
            let identityFilter = SearchProjectionFilters.sourceByIdentity identity

            let filter =
                SearchProjectionFilters.sourceAcceptingOperationalStatus identity status

            let operationalStatus = Some(toOperationalStatus status)

            let update =
                Builders<SearchSourceDocument>.Update.Set(_.OperationalStatus, operationalStatus)

            let! updated = sources.UpdateOneAsync(session, filter, update, cancellationToken = cancellationToken)

            if updated.MatchedCount = 0L then
                let! exists = sources.Find(session, identityFilter).Limit(1).AnyAsync(cancellationToken)

                if not exists then
                    let pendingSource =
                        { Id = SearchSourceDocument.createId()
                          ResourceTypeId = sourceType.ResourceTypeId.Value
                          SourceApiName = sourceType.ApiName
                          SourceSortOrder = sourceType.SortOrder
                          TenantId = identity.TenantId
                          SourceId = identity.SourceId
                          Title = ""
                          ResourceGroupId = None
                          ResourceGroupName = None
                          SearchKeys = [||]
                          OperationalStatus = operationalStatus }

                    do! sources.InsertOneAsync(session, pendingSource, cancellationToken = cancellationToken)
                    return true
                else
                    return false
            else
                return updated.ModifiedCount = 1L
        }

    member _.PersistDocumentSetAsync(documentSet: SearchDocumentSet, cancellationToken: CancellationToken) =
        task {
            let identity =
                SearchProjectionIdentity(
                    documentSet.TenantId,
                    documentSet.SourceType.ResourceTypeId,
                    documentSet.SourceId
                )

            let resourceRevision = documentSet.ResourceRevision

            if resourceRevision < 1L then
                invalidArg (nameof documentSet) "Search projection resource revision must be positive."

            use! session = database.Client.StartSessionAsync(cancellationToken = cancellationToken)

            let! _ =
                session.WithTransactionAsync(
                    (fun transaction transactionCancellationToken ->
                        task {
                            let versionFilter = SearchProjectionFilters.versionByIdentity identity

                            let! currentVersions =
                                versions
                                    .Find(transaction, versionFilter)
                                    .Limit(1)
                                    .ToListAsync(transactionCancellationToken)

                            let currentVersion = currentVersions |> Seq.tryHead

                            match currentVersion with
                            | Some current when current.ResourceRevision >= resourceRevision -> return false
                            | _ ->
                                let nextVersion =
                                    { Id =
                                        currentVersion
                                        |> Option.map _.Id
                                        |> Option.defaultWith SearchProjectionVersionDocument.createId
                                      ResourceTypeId = identity.ResourceTypeId.Value
                                      TenantId = identity.TenantId
                                      SourceId = identity.SourceId
                                      ResourceRevision = resourceRevision
                                      IsDeleted = false }

                                let! _ =
                                    versions.ReplaceOneAsync(
                                        transaction,
                                        versionFilter,
                                        nextVersion,
                                        ReplaceOptions(IsUpsert = true),
                                        transactionCancellationToken
                                    )

                                let documentFilter = SearchProjectionFilters.documentsBySource identity

                                do!
                                    documents.DeleteManyAsync(
                                        transaction,
                                        documentFilter,
                                        cancellationToken = transactionCancellationToken
                                    )
                                    :> Task

                                if not (List.isEmpty documentSet.Documents) then
                                    do!
                                        documents.InsertManyAsync(
                                            transaction,
                                            documentSet.Documents,
                                            cancellationToken = transactionCancellationToken
                                        )

                                let candidate = SearchDocumentFactory.toSourceDocument documentSet

                                let update =
                                    Builders<SearchSourceDocument>.Update
                                        .Set(_.SourceApiName, candidate.SourceApiName)
                                        .Set(_.SourceSortOrder, candidate.SourceSortOrder)
                                        .Set(_.Title, candidate.Title)
                                        .Set(_.ResourceGroupId, candidate.ResourceGroupId)
                                        .Set(_.ResourceGroupName, candidate.ResourceGroupName)
                                        .Set(_.SearchKeys, candidate.SearchKeys)
                                        .SetOnInsert(_.Id, candidate.Id)
                                        .SetOnInsert(_.ResourceTypeId, candidate.ResourceTypeId)
                                        .SetOnInsert(_.TenantId, candidate.TenantId)
                                        .SetOnInsert(_.SourceId, candidate.SourceId)
                                        .SetOnInsert(_.OperationalStatus, None)

                                let! _ =
                                    sources.UpdateOneAsync(
                                        transaction,
                                        SearchProjectionFilters.sourceByIdentity identity,
                                        update,
                                        UpdateOptions(IsUpsert = true),
                                        transactionCancellationToken
                                    )

                                do!
                                    storeChanged
                                        transaction
                                        identity
                                        resourceRevision
                                        (Nullable())
                                        transactionCancellationToken

                                return true
                        }),
                    cancellationToken = cancellationToken
                )

            return ()
        }

    member _.RemoveDocumentSetAsync
        (identity: SearchProjectionIdentity, resourceRevision: int64, cancellationToken: CancellationToken)
        =
        task {
            if resourceRevision < 1L then
                invalidArg (nameof resourceRevision) "Search projection resource revision must be positive."

            use! session = database.Client.StartSessionAsync(cancellationToken = cancellationToken)

            let! _ =
                session.WithTransactionAsync(
                    (fun transaction transactionCancellationToken ->
                        task {
                            let versionFilter = SearchProjectionFilters.versionByIdentity identity

                            let! currentVersions =
                                versions
                                    .Find(transaction, versionFilter)
                                    .Limit(1)
                                    .ToListAsync(transactionCancellationToken)

                            let currentVersion = currentVersions |> Seq.tryHead

                            match currentVersion with
                            | Some current when current.ResourceRevision >= resourceRevision -> return false
                            | _ ->
                                let tombstone =
                                    { Id =
                                        currentVersion
                                        |> Option.map _.Id
                                        |> Option.defaultWith SearchProjectionVersionDocument.createId
                                      ResourceTypeId = identity.ResourceTypeId.Value
                                      TenantId = identity.TenantId
                                      SourceId = identity.SourceId
                                      ResourceRevision = resourceRevision
                                      IsDeleted = true }

                                let! _ =
                                    versions.ReplaceOneAsync(
                                        transaction,
                                        versionFilter,
                                        tombstone,
                                        ReplaceOptions(IsUpsert = true),
                                        transactionCancellationToken
                                    )

                                do!
                                    documents.DeleteManyAsync(
                                        transaction,
                                        SearchProjectionFilters.documentsBySource identity,
                                        cancellationToken = transactionCancellationToken
                                    )
                                    :> Task

                                do!
                                    sources.DeleteManyAsync(
                                        transaction,
                                        SearchProjectionFilters.sourceByIdentity identity,
                                        cancellationToken = transactionCancellationToken
                                    )
                                    :> Task

                                do!
                                    storeChanged
                                        transaction
                                        identity
                                        resourceRevision
                                        (Nullable())
                                        transactionCancellationToken

                                return true
                        }),
                    cancellationToken = cancellationToken
                )

            return ()
        }

    member _.PersistOperationalStatusAsync
        (
            sourceType: SearchSourceType,
            identity: SearchProjectionIdentity,
            status: SearchOperationalStatusSnapshot,
            cancellationToken: CancellationToken
        ) =
        task {
            use! session = database.Client.StartSessionAsync(cancellationToken = cancellationToken)

            let! _ =
                session.WithTransactionAsync(
                    (fun transaction transactionCancellationToken ->
                        task {
                            let! currentVersions =
                                versions
                                    .Find(transaction, SearchProjectionFilters.versionByIdentity identity)
                                    .Limit(1)
                                    .ToListAsync(transactionCancellationToken)

                            let deletedAtSameOrNewerRevision =
                                currentVersions
                                |> Seq.tryHead
                                |> Option.exists (fun version ->
                                    version.IsDeleted && version.ResourceRevision >= status.ResourceRevision)

                            if deletedAtSameOrNewerRevision then
                                return false
                            else
                                let! changed =
                                    persistOperationalStatus
                                        transaction
                                        sourceType
                                        identity
                                        status
                                        transactionCancellationToken

                                if changed then
                                    do!
                                        storeChanged
                                            transaction
                                            identity
                                            status.ResourceRevision
                                            (Nullable status.ObservedGeneration)
                                            transactionCancellationToken

                                return changed
                        }),
                    cancellationToken = cancellationToken
                )

            return ()
        }

    member _.SearchAsync(query: SearchQuery, cancellationToken: CancellationToken) =
        task {
            let skip = (max query.PageNumber 1 - 1) * max query.PageSize 1
            let limit = max query.PageSize 1

            let regex =
                BsonRegularExpression($"^{Regex.Escape(SearchDocumentFactory.normalize query.Query)}")

            let pipeline =
                [| SearchProjectionFilters.getSourceMatchStage query
                   bsonDocument
                       [ BsonElement(
                             "$addFields",
                             bsonDocument
                                 [ BsonElement(
                                       "MatchedKeys",
                                       bsonDocument
                                           [ BsonElement(
                                                 "$filter",
                                                 bsonDocument
                                                     [ BsonElement("input", "$SearchKeys")
                                                       BsonElement("as", "key")
                                                       BsonElement(
                                                           "cond",
                                                           bsonDocument
                                                               [ BsonElement(
                                                                     "$regexMatch",
                                                                     bsonDocument
                                                                         [ BsonElement("input", "$$key.Value")
                                                                           BsonElement("regex", regex) ]
                                                                 ) ]
                                                       ) ]
                                             ) ]
                                   ) ]
                         ) ]
                   bsonDocument
                       [ BsonElement(
                             "$addFields",
                             bsonDocument
                                 [ BsonElement(
                                       "BestWeight",
                                       bsonDocument [ BsonElement("$max", "$MatchedKeys.Weight") ]
                                   ) ]
                         ) ]
                   bsonDocument
                       [ BsonElement(
                             "$sort",
                             bsonDocument
                                 [ BsonElement("BestWeight", -1)
                                   BsonElement("Title", -1)
                                   BsonElement("SourceSortOrder", 1)
                                   BsonElement("SourceId", 1) ]
                         ) ]
                   bsonDocument
                       [ BsonElement(
                             "$project",
                             bsonDocument [ BsonElement("MatchedKeys", 0); BsonElement("BestWeight", 0) ]
                         ) ]
                   bsonDocument
                       [ BsonElement(
                             "$facet",
                             bsonDocument
                                 [ BsonElement(
                                       "items",
                                       bsonArray
                                           [ bsonDocument [ BsonElement("$skip", skip) ]
                                             bsonDocument [ BsonElement("$limit", limit) ] ]
                                   )
                                   BsonElement(
                                       "totalCount",
                                       bsonArray [ bsonDocument [ BsonElement("$count", "value") ] ]
                                   ) ]
                         ) ] |]

            let! facet =
                sources
                    .Aggregate<BsonDocument>(
                        pipeline,
                        AggregateOptions(Comment = "Pole|Feature=Search|Operation=GetSearch")
                    )
                    .FirstOrDefaultAsync(cancellationToken)

            let items =
                if isNull facet || not (facet.Contains("items")) then
                    [||]
                else
                    facet["items"].AsBsonArray
                    |> Seq.map (fun value -> BsonSerializer.Deserialize<SearchSourceDocument>(value.AsBsonDocument))
                    |> Seq.toArray

            let totalCount =
                if isNull facet || not (facet.Contains("totalCount")) then
                    0L
                else
                    let counts = facet["totalCount"].AsBsonArray

                    if counts.Count = 0 then
                        0L
                    else
                        counts[0].AsBsonDocument["value"].ToInt64()

            return
                PaginatedResponse<SearchHitDto>(
                    items |> Array.map toSearchHit,
                    totalCount,
                    query.PageNumber,
                    query.PageSize
                )
        }
