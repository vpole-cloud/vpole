namespace Pole.Features.Postgres.Events

open MassTransit
open Pole.Domain.Models.FSharp
open Pole.Features.Events.Abstractions

[<EntityName("create-postgres-cluster-requested")>]
type CreateEvent =
    { ClusterId : string
      Name : string
      GroupId : string
      Spec : PostgresClusterSpec }
    interface IPoleEvent

module CreateEvent =
    let create (cluster : PostgresCluster) : CreateEvent =
        { ClusterId = cluster.Id
          Name = cluster.Name
          GroupId = cluster.GroupId
          Spec = cluster.Spec }

    let toPostgresCluster (event : CreateEvent) : PostgresCluster =
        { Id = event.ClusterId
          Name = event.Name
          GroupId = event.GroupId
          Spec = event.Spec }
