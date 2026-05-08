namespace Pole.Features.Search.Migrations

open System.Threading
open MongoDB.Bson
open MongoDB.Driver
open Pole.Database

type SearchDocumentIndexesMigration() =
    interface IMigration with
        member _.Id = ObjectId("69ce0000b213917a38c379ea")
        member _.Name = "Weighted search indexes"
        member _.Description = "Rebuilds SearchDocuments for the weighted-key search model and creates its indexes."

        member _.Up(database : IMongoDatabase, cancellationToken : CancellationToken) =
            task {
                let collectionName = "SearchDocuments"
                let sourceCollectionName = "SearchSourceDocuments"
                let! existingCollections = database.ListCollectionNames().ToListAsync(cancellationToken)

                if existingCollections |> Seq.contains collectionName then
                    do! database.DropCollectionAsync(collectionName, cancellationToken)

                if existingCollections |> Seq.contains sourceCollectionName then
                    do! database.DropCollectionAsync(sourceCollectionName, cancellationToken)

                let collection = database.GetCollection<BsonDocument>(collectionName)
                let sourceCollection = database.GetCollection<BsonDocument>(sourceCollectionName)
                let indexModels =
                    [|
                        CreateIndexModel<BsonDocument>(
                            Builders<BsonDocument>.IndexKeys
                                .Ascending("SourceType")
                                .Ascending("SourceId")
                                .Ascending("KeyType")
                                .Ascending("SearchValue"),
                            CreateIndexOptions(Unique = true, Name = "IX_SearchDocuments_Source_Key")
                        )
                        CreateIndexModel<BsonDocument>(
                            Builders<BsonDocument>.IndexKeys
                                .Ascending("SearchValue")
                                .Ascending("SourceType"),
                            CreateIndexOptions(Name = "IX_SearchDocuments_SearchValue_SourceType")
                        )
                        CreateIndexModel<BsonDocument>(
                            Builders<BsonDocument>.IndexKeys
                                .Ascending("SourceType")
                                .Ascending("SearchValue")
                                .Descending("Weight"),
                            CreateIndexOptions(Name = "IX_SearchDocuments_SourceType_SearchValue_Weight")
                        )
                    |]

                let! _ = collection.Indexes.CreateManyAsync(indexModels, cancellationToken)

                let sourceIndexModels =
                    [|
                        CreateIndexModel<BsonDocument>(
                            Builders<BsonDocument>.IndexKeys
                                .Ascending("SourceType")
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
                                .Ascending("SourceType")
                                .Ascending("SearchKeys.Value"),
                            CreateIndexOptions(Name = "IX_SearchSourceDocuments_SourceType_SearchKeys_Value")
                        )
                    |]

                let! _ = sourceCollection.Indexes.CreateManyAsync(sourceIndexModels, cancellationToken)
                return ()
            }
