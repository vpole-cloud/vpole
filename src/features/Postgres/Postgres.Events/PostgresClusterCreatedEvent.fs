namespace Pole.Features.Postgres.Events

open MassTransit
open Pole.Features.Events.Abstractions

type PostgresClusterSpecSnapshot =
    { Instances: int
      Storage: int
      StorageProfileId: string
      Version: int }

[<EntityName("pole.postgres/v1/postgres-cluster-created")>]
type PostgresClusterCreatedEvent =
    { EventCore: EventCore
      ClusterId: string
      Name: string
      GroupId: string
      ResourceRevision: int64
      Spec: PostgresClusterSpecSnapshot }

    interface IPoleEvent with
        member this.EventCore = this.EventCore
