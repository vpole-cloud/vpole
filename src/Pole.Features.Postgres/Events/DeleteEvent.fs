namespace Pole.Features.Postgres.Events

open Pole.Features.Core.Abstractions

type DeleteEvent =
    { ClusterId : string }
    interface IPoleEvent
    
module DeleteEvent =
    let create clusterId : DeleteEvent =
        { ClusterId = clusterId }
