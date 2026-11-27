// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Database;

namespace Pole.Features.Postgres.Tests.Integration.Infrastructure;

public static class MongoTestStoreTransactionExtensions
{
    public static Task<TOutcome> RunOutcomeInTransactionAsync<TOutcome>(
        this PostgresTestStore store,
        Func<MongoTransactionContext<TOutcome>, CancellationToken, Task<MongoTransactionDecision<TOutcome>>>
            callbackAsync,
        CancellationToken cancellationToken = default)
        where TOutcome : notnull =>
        new MongoRunTransaction(store.Client).InvokeOutcomeAsync(callbackAsync, cancellationToken);
}
