// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.Outbox.Application.Mongo;

namespace Pole.Features.Outbox.Application.Delivery;

public delegate Task<bool> TryPersistOutboxMessageTransition(
    OutboxMessage expectedPending,
    OutboxMessage transition,
    CancellationToken cancellationToken = default);

internal sealed class MongoTryPersistOutboxMessageTransition(
    IMongoCollection<OutboxMessage> messages
)
{
    public async Task<bool> InvokeAsync(
        OutboxMessage expectedPending,
        OutboxMessage transition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedPending);
        ArgumentNullException.ThrowIfNull(transition);
        if (expectedPending.Id != transition.Id)
            throw new ArgumentException("An outbox transition must retain the message identifier.", nameof(transition));

        if (expectedPending.State is not PendingOutboxMessageState)
            throw new ArgumentException(
                "An outbox transition must be based on a pending message.",
                nameof(expectedPending));

        var replacement = await messages.ReplaceOneAsync(
            OutboxMessageFilters.PendingAtAttempt(expectedPending),
            transition,
            cancellationToken: cancellationToken);
        return replacement.ModifiedCount == 1;
    }
}
