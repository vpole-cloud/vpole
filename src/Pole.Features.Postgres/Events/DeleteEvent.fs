namespace Pole.Features.Postgres.Events

open Pole.Features.Core.Abstractions

type DeleteEvent(clusterId : string) =
    inherit BaseEvent()

    member _.ClusterId = clusterId
    override _.``<Clone>$``() = DeleteEvent(clusterId) :> BaseEvent
