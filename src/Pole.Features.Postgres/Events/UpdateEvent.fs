namespace Pole.Features.Postgres.Events

open Pole.Features.Core.Abstractions

type UpdateEvent =
    { ClusterId : string
      NewPassword : string option }
    interface IPoleEvent

module UpdateEvent =
    let create clusterId newPassword : UpdateEvent =
        { ClusterId = clusterId
          NewPassword = newPassword }
