namespace Pole.Features.Postgres.Migrations

open System.Threading.Tasks
open MongoDB.Bson
open MongoDB.Driver
open Pole.Migrations

// Frozen persisted contract. Keep this independent of current Domain models and runtime constants.
module private PostgresManagedDatabaseDocumentSchema =
    let bsonType (name: string) = BsonDocument("bsonType", BsonString(name))

    let text () =
        BsonDocument([ BsonElement("bsonType", BsonString("string")); BsonElement("minLength", BsonInt32(1)) ])

    let stringIdentity () =
        BsonDocument([ BsonElement("bsonType", BsonString("string")); BsonElement("pattern", BsonString("^[a-fA-F0-9]{24}$")) ])

    let closedObject (required: (string * BsonDocument) list) (optional: (string * BsonDocument) list) =
        let properties =
            required @ optional
            |> Seq.map (fun (name, schema) -> BsonElement(name, schema))
            |> BsonDocument
        let names = required |> Seq.map (fun (name, _) -> BsonString(name) :> BsonValue) |> BsonArray
        BsonDocument(
            [ BsonElement("bsonType", BsonString("object"))
              BsonElement("required", names)
              BsonElement("additionalProperties", BsonBoolean(false))
              BsonElement("properties", properties) ])

    let stateVariant name (fields: (string * BsonDocument) list) =
        let discriminator = BsonArray([ BsonString("PostgresManagedDatabaseState"); BsonString(name) ])
        let allowed = BsonDocument("enum", BsonArray([ discriminator ]))
        closedObject (("_t", allowed) :: fields) []

    let oneOf (schemas: BsonDocument list) = BsonDocument("oneOf", BsonArray(schemas))

    let createValidator () =
        let state =
            oneOf
                [ stateVariant "ActivePostgresManagedDatabase" []
                  stateVariant "PurgingPostgresManagedDatabase" [ "RequestedAtUtc", bsonType "date" ]
                  stateVariant "PurgedPostgresManagedDatabase" [ "CompletedAtUtc", bsonType "date" ] ]
        // F# options serialize as null (None) or { Value: date } (Some); the record field is required.
        let audit = oneOf [ bsonType "null"; closedObject [ "Value", bsonType "date" ] [] ]
        let revision = BsonDocument([ BsonElement("bsonType", BsonString("long")); BsonElement("minimum", BsonInt64(1L)) ])
        let schema =
            closedObject
                [ "_id", bsonType "objectId"
                  "TenantId", bsonType "objectId"
                  "ClusterId", bsonType "objectId"
                  "Owner", closedObject [ "ResourceType", text (); "ResourceId", bsonType "objectId" ] []
                  "Source", closedObject
                      [ "ResourceGroupId", stringIdentity ()
                        "IsolationScopeId", stringIdentity ()
                        "DeploymentTargetId", stringIdentity ()
                        "Component", text () ] []
                  "Target", closedObject
                      [ "DeploymentId", stringIdentity ()
                        "DeploymentTargetId", stringIdentity ()
                        "RuntimeCellId", stringIdentity ()
                        "KubernetesNamespace", text () ] []
                  "CredentialId", bsonType "objectId"
                  "Database", text ()
                  "Username", text ()
                  "State", state
                  "Revision", revision
                  "CreatedAtUtc", bsonType "date"
                  "UpdatedAtUtc", bsonType "date"
                  "LastAuditAtUtc", audit ]
                []
        BsonDocument("$jsonSchema", schema)

type PostgresManagedDatabaseIndexesMigration() =
    interface IMigration with
        member _.Id = ObjectId("6a700056b213917a38c38056")
        member _.Name = "PostgreSQL managed database identity"
        member _.Description = "Strict managed database claim schema, tenant-owner uniqueness, cluster dependencies, and durable cleanup audit."
        member _.Up(context, cancellationToken) =
            task {
                let collectionName = "PostgresManagedDatabases"
                let validator = PostgresManagedDatabaseDocumentSchema.createValidator ()
                use! names = context.ListCollectionNamesAsync(
                    ListCollectionNamesOptions(Filter = BsonDocument("name", BsonString(collectionName))), cancellationToken)
                let! exists = names.AnyAsync(cancellationToken)
                if exists then
                    let command =
                        BsonDocument(
                            [ BsonElement("collMod", BsonString(collectionName))
                              BsonElement("validator", validator)
                              BsonElement("validationLevel", BsonString("strict"))
                              BsonElement("validationAction", BsonString("error")) ])
                    let! _ = context.RunCommandAsync<BsonDocument>(command, cancellationToken = cancellationToken)
                    ()
                else
                    let options = CreateCollectionOptions<BsonDocument>(
                        Validator = BsonDocumentFilterDefinition<BsonDocument>(validator),
                        ValidationLevel = DocumentValidationLevel.Strict,
                        ValidationAction = DocumentValidationAction.Error)
                    do! context.CreateCollectionAsync(collectionName, options, cancellationToken)
                let collection = context.GetCollection<BsonDocument>(collectionName)
                let models =
                    [| CreateIndexModel<BsonDocument>(
                           Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("Owner.ResourceType").Ascending("Owner.ResourceId"),
                           CreateIndexOptions(Unique = true, Name = "UX_PostgresManagedDatabases_Owner"))
                       CreateIndexModel<BsonDocument>(
                           Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("ClusterId"),
                           CreateIndexOptions(Name = "IX_PostgresManagedDatabases_Cluster")) |]
                let! _ = collection.Indexes.CreateManyAsync(models, cancellationToken = cancellationToken)
                return ()
            } :> Task
