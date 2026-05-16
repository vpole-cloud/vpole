namespace Pole.Features.Postgres.Events

open MassTransit
open Pole.Features.Postgres.Domain
open Pole.Features.Events.Abstractions

[<EntityName("create-postgres-cluster-requested")>]
type CreateEvent =
    { EventCore : EventCore
      ClusterId : string
      Name : string
      GroupId : string
      Spec : PostgresClusterSpec }
    interface IPoleEvent with
        member this.EventCore = this.EventCore

module CreateEvent =
    let create (cluster : PostgresCluster) : CreateEvent =
        { EventCore = EventCore.Create(cluster.TenantId)
          ClusterId = cluster.Id
          Name = cluster.Name
          GroupId = cluster.GroupId
          Spec = cluster.Spec }

    let toPostgresCluster (event : CreateEvent) : PostgresCluster =
        { Id = event.ClusterId
          TenantId = event.EventCore.TenantId
          Name = event.Name
          GroupId = event.GroupId
          Spec = event.Spec }
