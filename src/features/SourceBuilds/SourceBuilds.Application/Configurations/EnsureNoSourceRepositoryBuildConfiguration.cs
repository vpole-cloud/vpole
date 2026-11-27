// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.SourceBuilds.Application.Mongo;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Configurations;

internal sealed class MongoEnsureNoSourceRepositoryBuildConfiguration(
    IMongoCollection<SourceBuildConfiguration> configurations
)
{
    public async Task<EnsureNoSourceRepositoryBuildConfigurationOutcome> InvokeAsync(
        SourceRepositoryConfigurationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var hasConfiguration = await configurations.Find(
                session,
                SourceBuildConfigurationFilters.BySourceRepository(request.TenantId, request.SourceRepositoryId))
            .AnyAsync(cancellationToken);
        return hasConfiguration
            ? new SourceBuildSourceRepositoryConfigurationExists(request.SourceRepositoryId)
            : new NoSourceRepositoryBuildConfiguration();
    }
}
