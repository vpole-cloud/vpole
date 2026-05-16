namespace Pole.Features.Postgres.Events

open MassTransit
open Pole.Features.Postgres.Domain
open Pole.Features.Events.Abstractions

type UpdateEventChanges =
    { Spec : UpdateLog<PostgresClusterSpec> }

[<EntityName("update-postgres-cluster-requested")>]
type UpdateEvent =
    { EventCore : EventCore
      ClusterId : string
      Name : string
      GroupId : string
      Changes : UpdateEventChanges }
    interface IPoleEvent with
        member this.EventCore = this.EventCore

module UpdateEvent =
    let create (previous : PostgresCluster) (next : PostgresCluster) : UpdateEvent =
        { EventCore = EventCore.Create(next.TenantId)
          ClusterId = next.Id
          Name = next.Name
          GroupId = next.GroupId
          Changes =
            { Spec = UpdateLog<PostgresClusterSpec>.Of(previous.Spec, next.Spec) } }

    let toPostgresCluster (event : UpdateEvent) : PostgresCluster =
        { Id = event.ClusterId
          TenantId = event.EventCore.TenantId
          Name = event.Name
          GroupId = event.GroupId
          Spec = event.Changes.Spec.New }
