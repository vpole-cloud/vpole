namespace Pole.Features.Postgres.Events

open MassTransit
open Pole.Features.Postgres.Domain
open Pole.Features.Events.Abstractions

type PostgresClusterUpdatedEventChanges =
    { Spec : UpdateLog<PostgresClusterSpec> }

[<EntityName("postgres-cluster-updated")>]
type PostgresClusterUpdatedEvent =
    { EventCore : EventCore
      ClusterId : string
      Name : string
      GroupId : string
      Changes : PostgresClusterUpdatedEventChanges }
    interface IPoleEvent with
        member this.EventCore = this.EventCore

module PostgresClusterUpdatedEvent =
    let create (previous : PostgresCluster) (next : PostgresCluster) : PostgresClusterUpdatedEvent =
        { EventCore = EventCore.Create(next.TenantId)
          ClusterId = next.Id
          Name = next.Name
          GroupId = next.GroupId
          Changes =
            { Spec = UpdateLog<PostgresClusterSpec>.Of(previous.Spec, next.Spec) } }

    let toPostgresCluster (event : PostgresClusterUpdatedEvent) : PostgresCluster =
        { Id = event.ClusterId
          TenantId = event.EventCore.TenantId
          Name = event.Name
          GroupId = event.GroupId
          Spec = event.Changes.Spec.New }
