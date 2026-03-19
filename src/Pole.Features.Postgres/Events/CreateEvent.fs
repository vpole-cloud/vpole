namespace Pole.Features.Postgres.Events

open Pole.Features.Events.Abstractions

type CreateEvent =
    { ClusterId : string
      Password : string }
    interface IPoleEvent

module CreateEvent =
    let create clusterId password : CreateEvent =
        { ClusterId = clusterId
          Password = password }