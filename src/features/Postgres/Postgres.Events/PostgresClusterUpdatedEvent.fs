namespace Pole.Features.Postgres.Events

open MassTransit
open Pole.Features.Events.Abstractions

type PostgresClusterUpdatedEventChanges =
    { Spec : UpdateLog<PostgresClusterSpecSnapshot> }

[<EntityName("pole.postgres/v1/postgres-cluster-updated")>]
type PostgresClusterUpdatedEvent =
    { EventCore : EventCore
      ClusterId : string
      Name : string
      GroupId : string
      ResourceRevision : int64
      Changes : PostgresClusterUpdatedEventChanges }
    interface IPoleEvent with
        member this.EventCore = this.EventCore
