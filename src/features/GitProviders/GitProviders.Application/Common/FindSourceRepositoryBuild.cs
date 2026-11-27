// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.GitProviders.Application.Repositories;
using Pole.Features.GitProviders.Contracts;

namespace Pole.Features.GitProviders.Application.Common;

public sealed class SourceRepositoryBuildFinder(
    LoadSourceRepository loadSourceRepository
)
{
    public async Task<FindSourceRepositoryBuildOutcome> InvokeAsync(
        string tenantId,
        string sourceRepositoryId,
        CancellationToken cancellationToken = default)
    {
        var repository = await loadSourceRepository(tenantId, sourceRepositoryId, cancellationToken);
        return repository is null
            ? new SourceRepositoryBuildNotFound(sourceRepositoryId)
            : repository.ToBuildSnapshot();
    }
}
