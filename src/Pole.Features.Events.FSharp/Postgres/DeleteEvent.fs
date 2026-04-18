namespace Pole.Features.Events.FSharp.Postgres

open MassTransit
open Pole.Features.Events.Abstractions

[<EntityName("delete-postgres-cluster-requested")>]
type DeleteEvent =
    { ClusterId : string }
    interface IPoleEvent
    
module DeleteEvent =
    let create clusterId : DeleteEvent =
        { ClusterId = clusterId }
