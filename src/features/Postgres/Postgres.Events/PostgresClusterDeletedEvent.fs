namespace Pole.Features.Postgres.Events

open MassTransit
open Pole.Features.Postgres.Domain
open Pole.Features.Events.Abstractions

[<EntityName("postgres-cluster-deleted")>]
type PostgresClusterDeletedEvent =
    { EventCore : EventCore
      ClusterId : string
      Name : string
      GroupId : string
      Spec : PostgresClusterSpec }
    interface IPoleEvent with
        member this.EventCore = this.EventCore

module PostgresClusterDeletedEvent =
    let create (cluster : PostgresCluster) : PostgresClusterDeletedEvent =
        { EventCore = EventCore.Create(cluster.TenantId)
          ClusterId = cluster.Id
          Name = cluster.Name
          GroupId = cluster.GroupId
          Spec = cluster.Spec }
