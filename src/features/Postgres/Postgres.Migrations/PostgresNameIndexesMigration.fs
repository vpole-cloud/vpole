namespace Pole.Features.Postgres.Migrations

open System.Threading.Tasks
open MongoDB.Bson
open MongoDB.Driver
open Pole.Migrations

type PostgresNameIndexesMigration() =
    interface IMigration with
        member _.Id = ObjectId("6a700003b213917a38c38001")
        member _.Name = "PostgreSQL tenant name uniqueness"
        member _.Description = "Enforces unique PostgreSQL cluster names within each tenant."
        member _.Up(context, cancellationToken) =
            task {
                let model =
                    CreateIndexModel<BsonDocument>(
                        Builders<BsonDocument>.IndexKeys
                            .Ascending("TenantId")
                            .Ascending("Name"),
                        CreateIndexOptions(Unique = true, Name = "UX_Postgres_Tenant_Name"))
                let! _ =
                    context.GetCollection<BsonDocument>("Postgres").Indexes.CreateOneAsync(
                        model,
                        cancellationToken = cancellationToken)
                return ()
            } :> Task
