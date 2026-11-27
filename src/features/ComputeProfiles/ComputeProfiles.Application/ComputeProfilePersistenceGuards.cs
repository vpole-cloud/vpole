// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.ComputeProfiles.Domain;

namespace Pole.Features.ComputeProfiles.Application;

internal static class ComputeProfilePersistenceGuards
{
    public static void EnsureSameIdentity(ComputeProfile current, ComputeProfile replacement)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(replacement);
        if (!string.Equals(current.Id, replacement.Id, StringComparison.Ordinal) ||
            current.TechnicalName != replacement.TechnicalName)
            throw new InvalidOperationException("A compute profile replacement must preserve profile identity.");
    }
}
