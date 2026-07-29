namespace Pole.Features.Postgres.Application.Common

open System.Threading
open System.Threading.Tasks
open MongoDB.Bson
open MongoDB.Driver
open Pole.Database
open Pole.Features.Postgres.Domain

type PostgresCredentialMigration() =
    interface IMigration with
        member _.Id = ObjectId("69da0000b213917a38c379f8")
        member _.Name = "PostgreSQL durable credentials"
        member _.Description =
            "Creates tenant-safe uniqueness and lookup indexes for encrypted PostgreSQL credentials."

        member _.Up(context : IMongoDatabase, cancellationToken : CancellationToken) =
            task {
                let credentials =
                    context.GetCollection<BsonDocument>(PostgresCredential.CollectionName)

                let indexes =
                    [ CreateIndexModel<BsonDocument>(
                          Builders<BsonDocument>.IndexKeys
                              .Ascending("TenantId")
                              .Ascending("PostgresClusterId"),
                          CreateIndexOptions(
                              Unique = true,
                              Name = "UX_PostgresCredentials_Tenant_Cluster"))
                      CreateIndexModel<BsonDocument>(
                          Builders<BsonDocument>.IndexKeys
                              .Ascending("PostgresClusterId")
                              .Ascending("TenantId"),
                          CreateIndexOptions(
                              Name = "IX_PostgresCredentials_Cluster_Tenant")) ]

                let! _ =
                    credentials.Indexes.CreateManyAsync(
                        indexes,
                        cancellationToken = cancellationToken)

                return ()
            }
            :> Task
