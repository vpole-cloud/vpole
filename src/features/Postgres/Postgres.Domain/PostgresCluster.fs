namespace Pole.Features.Postgres.Domain

open System
open MongoDB.Bson
open MongoDB.Bson.Serialization.Attributes

type PostgresDeploymentMode =
    | Operator = 0
    | StatefulSet = 1

type PostgresClusterSpec =
    { Instances : int
      Storage : int
      StorageProfileId : string
      Version : int
      DeploymentMode : PostgresDeploymentMode }

[<AbstractClass>]
[<BsonDiscriminator(RootClass = true)>]
[<BsonKnownTypes(typeof<ActivePostgresClusterLifecycle>, typeof<DeletingPostgresClusterLifecycle>)>]
type PostgresClusterLifecycle() =
    class
    end

and [<BsonDiscriminator("active")>] ActivePostgresClusterLifecycle() =
    inherit PostgresClusterLifecycle()

and [<BsonDiscriminator("deleting")>] DeletingPostgresClusterLifecycle(requestedAtUtc : DateTime) =
    inherit PostgresClusterLifecycle()

    member _.RequestedAtUtc = requestedAtUtc

type PostgresCluster =
    { [<BsonId>]
      [<BsonRepresentation(BsonType.ObjectId)>]
      Id : string
      [<BsonRepresentation(BsonType.ObjectId)>]
      TenantId : string
      Name : string
      [<BsonRepresentation(BsonType.ObjectId)>]
      GroupId : string
      Spec : PostgresClusterSpec
      Revision : int64
      Lifecycle : PostgresClusterLifecycle }

    static member Create(
        tenantId : string,
        name : string,
        groupId : string,
        version : int,
        deploymentMode : PostgresDeploymentMode,
        storageProfileId : string,
        options : CreatePostgresClusterOptions) =
        if options.Instances < 1 then
            invalidArg (nameof options) "PostgreSQL instances must be positive."
        if options.Storage < 1 then
            invalidArg (nameof options) "PostgreSQL storage must be positive."

        { Id = defaultArg options.Id (ObjectId.GenerateNewId().ToString())
          TenantId = tenantId
          Name = name
          GroupId = groupId
          Spec =
            { Instances = options.Instances
              Storage = options.Storage
              StorageProfileId = storageProfileId
              Version = version
              DeploymentMode = deploymentMode }
          Revision = 1L
          Lifecycle = ActivePostgresClusterLifecycle() }

    member this.UpdateDesiredState(instances : int, storage : int) =
        if instances < 1 then
            invalidArg (nameof instances) "PostgreSQL instances must be positive."
        if storage < 1 then
            invalidArg (nameof storage) "PostgreSQL storage must be positive."

        match this.Lifecycle with
        | :? ActivePostgresClusterLifecycle ->
            { this with
                Spec =
                    { this.Spec with
                        Instances = instances
                        Storage = storage }
                Revision = PostgresCluster.incrementRevision this.Revision }
        | _ ->
            invalidOp "A deleting PostgreSQL cluster cannot be updated."

    member this.BeginDeletion(requestedAtUtc : DateTime) =
        match this.Lifecycle with
        | :? DeletingPostgresClusterLifecycle -> this
        | _ ->
            let normalizedRequestedAtUtc =
                match requestedAtUtc.Kind with
                | DateTimeKind.Utc -> requestedAtUtc
                | DateTimeKind.Local -> requestedAtUtc.ToUniversalTime()
                | _ -> DateTime.SpecifyKind(requestedAtUtc, DateTimeKind.Utc)

            { this with
                Lifecycle = DeletingPostgresClusterLifecycle(normalizedRequestedAtUtc)
                Revision = PostgresCluster.incrementRevision this.Revision }

    static member private incrementRevision(revision : int64) =
        if revision = Int64.MaxValue then
            raise (OverflowException("PostgreSQL cluster revision overflowed."))

        revision + 1L

and CreatePostgresClusterOptions =
    { Id : string option
      Instances : int
      Storage : int }

[<RequireQualifiedAccess>]
module PostgresCluster =
    [<Literal>]
    let DefaultInstances = 1

    [<Literal>]
    let DefaultStorage = 1
