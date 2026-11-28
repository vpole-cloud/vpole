// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Search.Application.Projection

open System.Threading
open Pole.Features.Search.Contracts

type internal MongoDeleteSearchProjection(store: SearchProjectionStore) =
    member _.InvokeAsync
        (identity: SearchProjectionIdentity, resourceRevision: int64, cancellationToken: CancellationToken)
        =
        store.RemoveDocumentSetAsync(identity, resourceRevision, cancellationToken)
