namespace Pole.Features.Postgres.Events

open MassTransit
open Pole.Features.Postgres.Domain
open Pole.Features.Events.Abstractions

[<EntityName("delete-postgres-cluster-requested")>]
type DeleteEvent =
    { EventCore : EventCore
      ClusterId : string
      Name : string
      GroupId : string
      Spec : PostgresClusterSpec }
    interface IPoleEvent with
        member this.EventCore = this.EventCore

module DeleteEvent =
    let create (cluster : PostgresCluster) : DeleteEvent =
        { EventCore = EventCore.Create(cluster.TenantId)
          ClusterId = cluster.Id
          Name = cluster.Name
          GroupId = cluster.GroupId
          Spec = cluster.Spec }
