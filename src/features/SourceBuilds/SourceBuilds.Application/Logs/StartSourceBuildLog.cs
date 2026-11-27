// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Logs;

public delegate Task<SourceBuildLogWriteOutcome> StartSourceBuildLog(
    SourceBuildLog log,
    CancellationToken cancellationToken = default);

internal sealed class MongoStartSourceBuildLog(
    SourceBuildLogPersistence persistence
)
{
    public Task<SourceBuildLogWriteOutcome> InvokeAsync(
        SourceBuildLog log,
        CancellationToken cancellationToken = default) => persistence.StartAsync(log, cancellationToken);
}
