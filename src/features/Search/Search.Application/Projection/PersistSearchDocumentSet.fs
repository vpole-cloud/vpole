// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Search.Application.Projection

open System.Threading
open Pole.Features.Search.Application.Common
open Pole.Features.Search.Application.Projection
open Pole.Features.Search.Contracts

type internal SearchProjectionWriter(sourceTypes: SearchSourceTypeRegistry, store: SearchProjectionStore) =

    member _.UpsertAsync(snapshot: SearchProjectionSnapshot, cancellationToken: CancellationToken) =
        match sourceTypes.Resolve(snapshot.Identity.ResourceTypeId) with
        | Some sourceType ->
            SearchDocumentFactory.fromSnapshot sourceType snapshot
            |> fun documentSet -> store.PersistDocumentSetAsync(documentSet, cancellationToken)
        | None -> invalidOp $"Search resource type '{snapshot.Identity.ResourceTypeId}' is not registered."
