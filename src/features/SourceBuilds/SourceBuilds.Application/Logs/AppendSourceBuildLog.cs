// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Logs;

public delegate Task<SourceBuildLogWriteOutcome> AppendSourceBuildLog(
    SourceBuildLogIdentity identity,
    long sequence,
    SourceBuildLogStream stream,
    string sanitizedText,
    CancellationToken cancellationToken = default);

internal sealed class MongoAppendSourceBuildLog(
    SourceBuildLogPersistence persistence
)
{
    public Task<SourceBuildLogWriteOutcome> InvokeAsync(
        SourceBuildLogIdentity identity,
        long sequence,
        SourceBuildLogStream stream,
        string sanitizedText,
        CancellationToken cancellationToken = default) => persistence.AppendAsync(
        identity,
        sequence,
        stream,
        sanitizedText,
        cancellationToken);
}
