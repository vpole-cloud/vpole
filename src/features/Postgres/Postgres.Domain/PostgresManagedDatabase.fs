namespace Pole.Features.Postgres.Domain

open System
open MongoDB.Bson
open MongoDB.Bson.Serialization.Attributes
open Pole.Multitenancy.Primitives

type PostgresDatabaseOwner =
    { ResourceType: string
      [<BsonRepresentation(BsonType.ObjectId)>]
      ResourceId: string }

type PostgresDatabaseSource =
    { ResourceGroupId: string
      IsolationScopeId: string
      DeploymentTargetId: string
      Component: string }

// Retained cleanup authority; ordinary workload placement remains owned by ResourceDeployments.
type PostgresDatabaseTarget =
    { DeploymentId: string
      DeploymentTargetId: string
      RuntimeCellId: string
      KubernetesNamespace: string }

[<AbstractClass>]
[<BsonDiscriminator(RootClass = true)>]
[<BsonKnownTypes(typeof<ActivePostgresManagedDatabase>, typeof<PurgingPostgresManagedDatabase>, typeof<PurgedPostgresManagedDatabase>)>]
type PostgresManagedDatabaseState() = class end

and ActivePostgresManagedDatabase() = inherit PostgresManagedDatabaseState()
and PurgingPostgresManagedDatabase(requestedAtUtc: DateTime) =
    inherit PostgresManagedDatabaseState()
    member _.RequestedAtUtc = requestedAtUtc
and PurgedPostgresManagedDatabase(completedAtUtc: DateTime) =
    inherit PostgresManagedDatabaseState()
    member _.CompletedAtUtc = completedAtUtc

type PostgresManagedDatabase =
    { [<BsonId>]
      [<BsonRepresentation(BsonType.ObjectId)>]
      Id: string
      [<BsonRepresentation(BsonType.ObjectId)>]
      TenantId: string
      [<BsonRepresentation(BsonType.ObjectId)>]
      ClusterId: string
      Owner: PostgresDatabaseOwner
      Source: PostgresDatabaseSource
      Target: PostgresDatabaseTarget
      [<BsonRepresentation(BsonType.ObjectId)>]
      CredentialId: string
      Database: string
      Username: string
      State: PostgresManagedDatabaseState
      Revision: int64
      CreatedAtUtc: DateTime
      UpdatedAtUtc: DateTime
      LastAuditAtUtc: DateTime option }

    interface ITenantOwned with member this.TenantId = this.TenantId

    [<BsonIgnore>]
    member this.IsActive = this.State :? ActivePostgresManagedDatabase

    [<BsonIgnore>]
    member this.IsPurged = this.State :? PurgedPostgresManagedDatabase

    member this.BeginPurge(nowUtc: DateTime) =
        match this.State with
        | :? ActivePostgresManagedDatabase ->
            { this with State = PurgingPostgresManagedDatabase(nowUtc); Revision = Checked.(+) this.Revision 1L; UpdatedAtUtc = nowUtc }
        | :? PurgingPostgresManagedDatabase | :? PurgedPostgresManagedDatabase -> this
        | _ -> invalidOp "Unknown managed PostgreSQL database state."

    member this.CompletePurge(nowUtc: DateTime) =
        match this.State with
        | :? PurgingPostgresManagedDatabase ->
            { this with State = PurgedPostgresManagedDatabase(nowUtc); Revision = Checked.(+) this.Revision 1L; UpdatedAtUtc = nowUtc }
        | :? PurgedPostgresManagedDatabase -> this
        | :? ActivePostgresManagedDatabase -> invalidOp "Managed database purge must be started first."
        | _ -> invalidOp "Unknown managed PostgreSQL database state."

    static member Create(tenantId: string, clusterId: string, owner: PostgresDatabaseOwner, source: PostgresDatabaseSource, target: PostgresDatabaseTarget, credentialId: string, nowUtc: DateTime) =
        for value in [ tenantId; clusterId; owner.ResourceId; source.ResourceGroupId; source.IsolationScopeId;
                       source.DeploymentTargetId; target.DeploymentId; target.DeploymentTargetId; target.RuntimeCellId; credentialId ] do
            if not (ObjectId.TryParse(value) |> fst) then invalidArg "identity" "Managed database identities must be ObjectIds."
        if String.IsNullOrWhiteSpace owner.ResourceType || String.IsNullOrWhiteSpace source.Component ||
           String.IsNullOrWhiteSpace target.KubernetesNamespace || source.DeploymentTargetId <> target.DeploymentTargetId then
            invalidArg "source" "An exact owner, component and same-target placement are required."
        if nowUtc.Kind <> DateTimeKind.Utc then invalidArg "nowUtc" "UTC is required."
        let id = ObjectId.GenerateNewId().ToString()
        { Id = id; TenantId = tenantId; ClusterId = clusterId; Owner = owner; Source = source; Target = target
          CredentialId = credentialId; Database = $"pole_db_{id}"; Username = $"pole_role_{id}"
          State = ActivePostgresManagedDatabase(); Revision = 1L; CreatedAtUtc = nowUtc; UpdatedAtUtc = nowUtc; LastAuditAtUtc = None }
