// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.SourceBuilds.Application.Logs;

public delegate Task<SourceBuildLogWriteOutcome> SealSourceBuildLog(
    SourceBuildLogIdentity identity,
    bool unavailable,
    CancellationToken cancellationToken = default);

internal sealed class MongoSealSourceBuildLog(
    SourceBuildLogPersistence persistence
)
{
    public Task<SourceBuildLogWriteOutcome> InvokeAsync(
        SourceBuildLogIdentity identity,
        bool unavailable,
        CancellationToken cancellationToken = default) =>
        persistence.SealAsync(identity, unavailable, cancellationToken);
}
