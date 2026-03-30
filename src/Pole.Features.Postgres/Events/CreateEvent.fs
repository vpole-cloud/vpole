namespace Pole.Features.Postgres.Events

open MassTransit
open Pole.Features.Events.Abstractions

[<EntityName("create-postgres-cluster-requested")>]
type CreateEvent =
    { ClusterId : string
      Password : string }
    interface IPoleEvent

module CreateEvent =
    let create clusterId password : CreateEvent =
        { ClusterId = clusterId
          Password = password }
