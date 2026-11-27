// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Application.Mongo;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Catalog;

public delegate Task<string?> LoadDefaultDeploymentTargetId(CancellationToken cancellationToken = default);

internal sealed class MongoLoadDefaultDeploymentTargetId(
    IMongoCollection<DeploymentTargetSettings> settings
)
{
    public async Task<string?> InvokeAsync(CancellationToken cancellationToken = default) => await settings
        .Find(DeploymentTargetSettingsFilters.Singleton())
        .Project(item => item.DefaultDeploymentTargetId)
        .FirstOrDefaultAsync(cancellationToken);
}
