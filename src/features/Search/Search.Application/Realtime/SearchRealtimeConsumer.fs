// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Search.Application.Realtime

open System.Threading
open MassTransit
open Pole.Features.Realtime.Contracts
open Pole.Features.Search.Events

type NotifySearchProjectionChangedConsumer(publish: PublishRealtimeChange) =
    member _.HandleAsync(message: SearchProjectionChangedEvent, ct: CancellationToken) =
        publish.Invoke(RealtimeChange("search", message.EventCore.TenantId), ct)

    interface IConsumer<SearchProjectionChangedEvent> with
        member this.Consume(context) =
            this.HandleAsync(context.Message, context.CancellationToken)
