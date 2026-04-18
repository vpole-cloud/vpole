namespace Pole.Features.Events.FSharp.Postgres

open MassTransit
open Pole.Features.Events.Abstractions

[<EntityName("update-postgres-cluster-requested")>]
type UpdateEvent =
    { ClusterId : string
      NewPassword : string option }
    interface IPoleEvent

module UpdateEvent =
    let create clusterId newPassword : UpdateEvent =
        { ClusterId = clusterId
          NewPassword = newPassword }
