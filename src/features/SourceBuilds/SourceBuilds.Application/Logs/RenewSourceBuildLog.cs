// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.SourceBuilds.Application.Logs;

public delegate Task<SourceBuildLogWriteOutcome> RenewSourceBuildLog(
    SourceBuildLogIdentity identity,
    CancellationToken cancellationToken = default);

internal sealed class MongoRenewSourceBuildLog(
    SourceBuildLogPersistence persistence
)
{
    public Task<SourceBuildLogWriteOutcome> InvokeAsync(
        SourceBuildLogIdentity identity,
        CancellationToken cancellationToken = default) => persistence.RenewAsync(identity, cancellationToken);
}
