namespace Pole.Features.Postgres.Events

open Pole.Features.Events.Abstractions

type DeleteEvent =
    { ClusterId : string }
    interface IPoleEvent
    
module DeleteEvent =
    let create clusterId : DeleteEvent =
        { ClusterId = clusterId }
