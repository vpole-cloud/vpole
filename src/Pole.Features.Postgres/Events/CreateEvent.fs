namespace Pole.Features.Postgres.Events

open Pole.Features.Core.Abstractions

type CreateEvent(clusterId : string) =
    inherit BaseEvent()

    member _.ClusterId = clusterId
    override _.``<Clone>$``() = CreateEvent(clusterId) :> BaseEvent
