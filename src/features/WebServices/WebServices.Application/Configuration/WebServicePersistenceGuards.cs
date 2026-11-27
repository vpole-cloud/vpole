// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Configuration;

internal static class WebServicePersistenceGuards
{
    public static void EnsureSameIdentity(WebService current, WebService replacement)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(replacement);
        if (!string.Equals(current.TenantId, replacement.TenantId, StringComparison.Ordinal) ||
            !string.Equals(current.Id, replacement.Id, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "A web service replacement must preserve tenant and resource identity.");
    }

    public static void EnsureTransaction(IClientSessionHandle session, string operation)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsInTransaction)
            throw new InvalidOperationException($"{operation} must run inside a transaction.");
    }
}
