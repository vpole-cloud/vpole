namespace Pole.Features.Postgres.Events

open MassTransit
open Pole.Features.Events.Abstractions

[<EntityName("postgres-cluster-deleted")>]
type PostgresClusterDeletedEvent =
    { EventCore : EventCore
      ClusterId : string
      Name : string
      GroupId : string
      Spec : PostgresClusterSpecSnapshot }
    interface IPoleEvent with
        member this.EventCore = this.EventCore
