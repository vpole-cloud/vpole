// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Search.Application.Projection

open System.Threading
open Pole.Features.Search.Application.Common
open Pole.Features.Search.Contracts

type internal MongoUpdateSearchOperationalStatus(sourceTypes: SearchSourceTypeRegistry, store: SearchProjectionStore) =

    member _.InvokeAsync
        (
            identity: SearchProjectionIdentity,
            status: SearchOperationalStatusSnapshot,
            cancellationToken: CancellationToken
        ) =
        match sourceTypes.Resolve(identity.ResourceTypeId) with
        | Some sourceType -> store.PersistOperationalStatusAsync(sourceType, identity, status, cancellationToken)
        | None -> invalidOp $"Search resource type '{identity.ResourceTypeId}' is not registered."
