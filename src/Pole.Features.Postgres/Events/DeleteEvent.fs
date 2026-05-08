namespace Pole.Features.Postgres.Events

open MassTransit
open Pole.Domain.Models.FSharp
open Pole.Features.Events.Abstractions

[<EntityName("delete-postgres-cluster-requested")>]
type DeleteEvent =
    { ClusterId : string
      Name : string
      GroupId : string
      Spec : PostgresClusterSpec }
    interface IPoleEvent

module DeleteEvent =
    let create (cluster : PostgresCluster) : DeleteEvent =
        { ClusterId = cluster.Id
          Name = cluster.Name
          GroupId = cluster.GroupId
          Spec = cluster.Spec }
