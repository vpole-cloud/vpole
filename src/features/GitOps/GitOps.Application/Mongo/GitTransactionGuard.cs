// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;

namespace Pole.Features.GitOps.Application.Mongo;

internal static class GitTransactionGuard
{
    public static void Require(IClientSessionHandle session)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Git ownership requires the resource transaction.");
    }
}
