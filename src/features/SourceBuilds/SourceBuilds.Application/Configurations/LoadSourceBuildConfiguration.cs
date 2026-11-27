// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.SourceBuilds.Application.Mongo;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Configurations;

public delegate Task<SourceBuildConfiguration?> LoadSourceBuildConfiguration(
    string tenantId,
    string configurationId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadSourceBuildConfiguration(
    IMongoCollection<SourceBuildConfiguration> configurations
)
{
    public async Task<SourceBuildConfiguration?> InvokeAsync(
        string tenantId,
        string configurationId,
        CancellationToken cancellationToken = default) => await configurations
        .Find(SourceBuildConfigurationFilters.ById(tenantId, configurationId))
        .FirstOrDefaultAsync(cancellationToken);
}
