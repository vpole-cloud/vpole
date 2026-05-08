namespace Pole.Features.Postgres.Events

open MassTransit
open Pole.Domain.Models.FSharp
open Pole.Features.Events.Abstractions

type UpdateEventChanges =
    { Spec : UpdateLog<PostgresClusterSpec> }

[<EntityName("update-postgres-cluster-requested")>]
type UpdateEvent =
    { ClusterId : string
      Name : string
      GroupId : string
      Changes : UpdateEventChanges }
    interface IPoleEvent

module UpdateEvent =
    let create (previous : PostgresCluster) (next : PostgresCluster) : UpdateEvent =
        { ClusterId = next.Id
          Name = next.Name
          GroupId = next.GroupId
          Changes =
            { Spec = UpdateLog<PostgresClusterSpec>.Of(previous.Spec, next.Spec) } }

    let toPostgresCluster (event : UpdateEvent) : PostgresCluster =
        { Id = event.ClusterId
          Name = event.Name
          GroupId = event.GroupId
          Spec = event.Changes.Spec.New }
