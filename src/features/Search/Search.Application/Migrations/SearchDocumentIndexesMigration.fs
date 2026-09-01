namespace Pole.Features.Search.Application.Migrations

open System.Threading
open MongoDB.Bson
open MongoDB.Driver
open Pole.Database

type SearchDocumentIndexesMigration() =
    interface IMigration with
        member _.Id = ObjectId("69ce0000b213917a38c379ea")
        member _.Name = "Weighted search indexes"
        member _.Description = "Creates Search projection collections and indexes for extensible source types."

        member _.Up(database : IMongoDatabase, cancellationToken : CancellationToken) =
            task {
                let collectionName = "SearchDocuments"
                let sourceCollectionName = "SearchSourceDocuments"
                let versionCollectionName = "SearchProjectionVersions"
                let! existingCollections = database.ListCollectionNames().ToListAsync(cancellationToken)

                if existingCollections |> Seq.contains collectionName then
                    do! database.DropCollectionAsync(collectionName, cancellationToken)

                if existingCollections |> Seq.contains sourceCollectionName then
                    do! database.DropCollectionAsync(sourceCollectionName, cancellationToken)

                if existingCollections |> Seq.contains versionCollectionName then
                    do! database.DropCollectionAsync(versionCollectionName, cancellationToken)

                let collection = database.GetCollection<BsonDocument>(collectionName)
                let sourceCollection = database.GetCollection<BsonDocument>(sourceCollectionName)
                let versionCollection = database.GetCollection<BsonDocument>(versionCollectionName)
                let indexModels =
                    [|
                        CreateIndexModel<BsonDocument>(
                            Builders<BsonDocument>.IndexKeys
                                .Ascending("TenantId")
                                .Ascending("ResourceTypeId")
                                .Ascending("SourceId")
                                .Ascending("KeyType")
                                .Ascending("SearchValue"),
                            CreateIndexOptions(Unique = true, Name = "IX_SearchDocuments_Source_Key")
                        )
                        CreateIndexModel<BsonDocument>(
                            Builders<BsonDocument>.IndexKeys
                                .Ascending("SearchValue")
                                .Ascending("SourceApiName"),
                            CreateIndexOptions(Name = "IX_SearchDocuments_SearchValue_SourceApiName")
                        )
                        CreateIndexModel<BsonDocument>(
                            Builders<BsonDocument>.IndexKeys
                                .Ascending("SourceSortOrder")
                                .Ascending("SearchValue")
                                .Descending("Weight"),
                            CreateIndexOptions(Name = "IX_SearchDocuments_SourceSortOrder_SearchValue_Weight")
                        )
                    |]

                let! _ = collection.Indexes.CreateManyAsync(indexModels, cancellationToken)

                let sourceIndexModels =
                    [|
                        CreateIndexModel<BsonDocument>(
                            Builders<BsonDocument>.IndexKeys
                                .Ascending("TenantId")
                                .Ascending("ResourceTypeId")
                                .Ascending("SourceId"),
                            CreateIndexOptions(Unique = true, Name = "IX_SearchSourceDocuments_Source")
                        )
                        CreateIndexModel<BsonDocument>(
                            Builders<BsonDocument>.IndexKeys
                                .Ascending("SearchKeys.Value"),
                            CreateIndexOptions(Name = "IX_SearchSourceDocuments_SearchKeys_Value")
                        )
                        CreateIndexModel<BsonDocument>(
                            Builders<BsonDocument>.IndexKeys
                                .Ascending("SourceApiName")
                                .Ascending("SearchKeys.Value"),
                            CreateIndexOptions(Name = "IX_SearchSourceDocuments_SourceApiName_SearchKeys_Value")
                        )
                    |]

                let! _ = sourceCollection.Indexes.CreateManyAsync(sourceIndexModels, cancellationToken)

                let versionIndex =
                    CreateIndexModel<BsonDocument>(
                        Builders<BsonDocument>.IndexKeys
                            .Ascending("TenantId")
                            .Ascending("ResourceTypeId")
                            .Ascending("SourceId"),
                        CreateIndexOptions(Unique = true, Name = "IX_SearchProjectionVersions_Source")
                    )
                let! _ = versionCollection.Indexes.CreateOneAsync(versionIndex, cancellationToken = cancellationToken)
                return ()
            }
