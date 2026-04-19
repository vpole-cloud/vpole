namespace Pole.Features.Events.FSharp.Postgres

open MassTransit
open Pole.Features.Events.Abstractions

[<EntityName("update-postgres-cluster-requested")>]
type UpdateEvent =
    { ClusterId : string }
    interface IPoleEvent

module UpdateEvent =
    let create clusterId : UpdateEvent =
        { ClusterId = clusterId }
