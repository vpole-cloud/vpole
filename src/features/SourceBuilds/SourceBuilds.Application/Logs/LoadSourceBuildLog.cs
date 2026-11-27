// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Logs;

public delegate Task<SourceBuildLog?> LoadSourceBuildLog(
    string tenantId,
    string webServiceId,
    string jobId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadSourceBuildLog(
    SourceBuildLogPersistence persistence
)
{
    public Task<SourceBuildLog?> InvokeAsync(
        string tenantId,
        string webServiceId,
        string jobId,
        CancellationToken cancellationToken = default) =>
        persistence.LoadAsync(tenantId, webServiceId, jobId, cancellationToken);
}
