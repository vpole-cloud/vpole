namespace Pole.Features.Postgres.Events

open MassTransit
open Pole.Features.Events.Abstractions

[<EntityName("delete-postgres-cluster-requested")>]
type DeleteEvent =
    { ClusterId : string }
    interface IPoleEvent
    
module DeleteEvent =
    let create clusterId : DeleteEvent =
        { ClusterId = clusterId }
