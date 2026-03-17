namespace Pole.Features.Postgres.Events

open Pole.Features.Core.Abstractions

type UpdateEvent(clusterId : string) =
    inherit BaseEvent()

    member _.ClusterId = clusterId
    override _.``<Clone>$``() = UpdateEvent(clusterId) :> BaseEvent
