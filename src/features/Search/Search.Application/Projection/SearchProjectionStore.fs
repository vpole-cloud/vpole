namespace Pole.Features.Search.Application.Projection

open Pole.Features.Search.Application.Mongo

open System
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open MongoDB.Bson
open Pole.Architecture
open MongoDB.Bson.Serialization
open MongoDB.Driver
open Pole.Database
open Pole.Features.Events.Abstractions
open Pole.Features.Outbox.Contracts
open Pole.Features.Request
open Pole.Features.Search.Application.Common
open Pole.Features.Search.Application.Projection
open Pole.Features.Search.Contracts
open Pole.Features.Search.Domain
open Pole.Features.Search.Events

type internal SearchProjectionStore(
    client: IMongoClient,
    documents: IMongoCollection<SearchDocument>,
    sources: IMongoCollection<SearchSourceDocument>,
    versions: IMongoCollection<SearchProjectionVersionDocument>,
    outbox: StoreOutboxWrites
) =

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

            use! session = client.StartSessionAsync(cancellationToken = cancellationToken)

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

            use! session = client.StartSessionAsync(cancellationToken = cancellationToken)

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
            use! session = client.StartSessionAsync(cancellationToken = cancellationToken)

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

    [<ViolateArchitecture(ArchitectureRules.RawBson,
        "Search computes regex matches, weights and pagination in one aggregation; decoding stays in this method.")>]
    member _.SearchAsync(query: SearchQuery, cancellationToken: CancellationToken) =
        task {
            let bsonDocument (elements: BsonElement list) = BsonDocument(elements)
            let bsonArray (values: BsonValue list) = BsonArray(values)
            let registry = BsonSerializer.SerializerRegistry
            let arguments = RenderArgs<SearchSourceDocument>(registry.GetSerializer<SearchSourceDocument>(), registry)
            let matches = SearchProjectionFilters.sourceMatch query
            let matchStage = bsonDocument [ BsonElement("$match", matches.Render(arguments)) ]

            let searchKeyValueField =
                MongoFields.Field<SearchSourceKey, _>(fun key -> key.Value)

            let searchKeyWeightField =
                MongoFields.Field<SearchSourceKey, _>(fun key -> key.Weight)

            let matchedKeysField =
                MongoFields.Field<SearchMatchedFields, _>(fun fields -> fields.MatchedKeys)

            let bestWeightField =
                MongoFields.Field<SearchMatchedFields, _>(fun fields -> fields.BestWeight)

            let itemsField = MongoFields.Field<SearchFacetResult, _>(fun result -> result.Items)
            let totalCountField = MongoFields.Field<SearchFacetResult, _>(fun result -> result.TotalCount)
            let countValueField = MongoFields.Field<SearchFacetCount, _>(fun count -> count.Value)
            let titleField = MongoFields.Field<SearchSourceDocument, _>(fun document -> document.Title)

            let sourceSortOrderField =
                MongoFields.Field<SearchSourceDocument, _>(fun document -> document.SourceSortOrder)

            let sourceIdField = MongoFields.Field<SearchSourceDocument, _>(fun document -> document.SourceId)

            let skip = (max query.PageNumber 1 - 1) * max query.PageSize 1
            let limit = max query.PageSize 1

            let regex =
                BsonRegularExpression($"^{Regex.Escape(SearchDocumentFactory.normalize query.Query)}")

            let pipeline =
                [| matchStage
                   bsonDocument
                       [ BsonElement(
                             "$addFields",
                             bsonDocument
                                 [ BsonElement(
                                       matchedKeysField,
                                       bsonDocument
                                           [ BsonElement(
                                                 "$filter",
                                                 bsonDocument
                                                     [ BsonElement(
                                                           "input",
                                                           MongoFields.Reference<SearchSourceDocument, _>(
                                                               fun document -> document.SearchKeys)
                                                       )
                                                       BsonElement("as", "key")
                                                       BsonElement(
                                                           "cond",
                                                           bsonDocument
                                                               [ BsonElement(
                                                                     "$regexMatch",
                                                                     bsonDocument
                                                                         [ BsonElement(
                                                                               "input",
                                                                               BsonString(
                                                                                   "$$key." + searchKeyValueField))
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
                                       bestWeightField,
                                       bsonDocument
                                           [ BsonElement(
                                                 "$max",
                                                 BsonString(
                                                     MongoFields.Reference<SearchMatchedFields, _>(
                                                         fun fields -> fields.MatchedKeys)
                                                     + "." + searchKeyWeightField)
                                             ) ]
                                   ) ]
                         ) ]
                   bsonDocument
                       [ BsonElement(
                             "$sort",
                             bsonDocument
                                 [ BsonElement(bestWeightField, -1)
                                   BsonElement(titleField, -1)
                                   BsonElement(sourceSortOrderField, 1)
                                   BsonElement(sourceIdField, 1) ]
                         ) ]
                   bsonDocument
                       [ BsonElement(
                             "$project",
                             bsonDocument [ BsonElement(matchedKeysField, 0); BsonElement(bestWeightField, 0) ]
                         ) ]
                   bsonDocument
                       [ BsonElement(
                             "$facet",
                             bsonDocument
                                 [ BsonElement(
                                       itemsField,
                                       bsonArray
                                           [ bsonDocument [ BsonElement("$skip", skip) ]
                                             bsonDocument [ BsonElement("$limit", limit) ] ]
                                   )
                                   BsonElement(
                                       totalCountField,
                                       bsonArray [ bsonDocument [ BsonElement("$count", countValueField) ] ]
                                   ) ]
                         ) ] |]

            let! facet =
                sources
                    .Aggregate<SearchFacetResult>(
                        pipeline,
                        AggregateOptions(Comment = "Pole|Feature=Search|Operation=GetSearch")
                    )
                    .FirstOrDefaultAsync(cancellationToken)

            let items =
                if isNull (box facet) || isNull facet.Items then
                    [||]
                else
                    facet.Items

            let totalCount =
                if isNull (box facet) || isNull facet.TotalCount then
                    0L
                else
                    let counts = facet.TotalCount

                    if counts.Length = 0 then
                        0L
                    else
                        counts[0].Value

            return
                PaginatedResponse<SearchHitDto>(
                    items |> Array.map toSearchHit,
                    totalCount,
                    query.PageNumber,
                    query.PageSize
                )
        }
