namespace Pole.Features.Postgres.Events

open MassTransit
open Pole.Features.Events.Abstractions

type PostgresClusterUpdatedEventChanges =
    { Spec : UpdateLog<PostgresClusterSpecSnapshot> }

[<EntityName("postgres-cluster-updated")>]
type PostgresClusterUpdatedEvent =
    { EventCore : EventCore
      ClusterId : string
      Name : string
      GroupId : string
      Changes : PostgresClusterUpdatedEventChanges }
    interface IPoleEvent with
        member this.EventCore = this.EventCore
