// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;

namespace Pole.Features.Outbox.Contracts;

/// <summary>
///     Stores writes in the caller-owned MongoDB transaction.
/// </summary>
public delegate Task StoreOutboxWrites(
    IClientSessionHandle session,
    IReadOnlyCollection<OutboxWrite> writes,
    CancellationToken cancellationToken = default);
