// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.SourceBuilds.Application.Logs;

public delegate Task RecoverSourceBuildLogs(CancellationToken cancellationToken = default);

internal sealed class MongoRecoverSourceBuildLogs(
    SourceBuildLogPersistence persistence
)
{
    public Task InvokeAsync(CancellationToken cancellationToken = default) =>
        persistence.RecoverAsync(cancellationToken);
}
