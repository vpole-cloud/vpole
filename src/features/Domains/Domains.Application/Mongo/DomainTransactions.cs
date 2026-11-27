// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;

namespace Pole.Features.Domains.Application.Mongo;

internal static class DomainTransactions
{
    public static void EnsureActive(IClientSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsInTransaction)
            throw new InvalidOperationException(
                "Domain mutations must run inside the caller-owned MongoDB transaction.");
    }
}
