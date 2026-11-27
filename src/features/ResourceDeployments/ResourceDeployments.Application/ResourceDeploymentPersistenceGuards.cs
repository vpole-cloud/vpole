// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;

namespace Pole.Features.ResourceDeployments.Application;

internal static class ResourceDeploymentPersistenceGuards
{
    public static void EnsureActiveTransaction(IClientSessionHandle session, string operation)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsInTransaction)
            throw new InvalidOperationException($"{operation} must run inside the logical resource transaction.");
    }
}
