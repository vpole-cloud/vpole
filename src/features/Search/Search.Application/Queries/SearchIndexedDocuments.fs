// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Search.Application.Queries

open Pole.Features.Search.Application.Projection

open System.Threading
open System.Threading.Tasks
open Pole.Features.Request
open Pole.Features.Search.Application.Common

type SearchIndexedDocuments =
    delegate of query: SearchQuery * cancellationToken: CancellationToken -> Task<PaginatedResponse<SearchHitDto>>

type internal MongoSearchIndexedDocuments(store: SearchProjectionStore) =
    member _.InvokeAsync(query: SearchQuery, cancellationToken: CancellationToken) =
        store.SearchAsync(query, cancellationToken)
