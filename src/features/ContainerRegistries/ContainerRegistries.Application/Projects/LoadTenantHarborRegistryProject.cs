// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.ContainerRegistries.Application.Mongo;
using Pole.Features.ContainerRegistries.Domain;

namespace Pole.Features.ContainerRegistries.Application.Projects;

public delegate Task<RegistryProject?> LoadTenantHarborRegistryProject(
    string tenantId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadTenantHarborRegistryProject(
    IMongoCollection<RegistryProject> registryProjects
)
{
    public async Task<RegistryProject?> InvokeAsync(string tenantId, CancellationToken cancellationToken = default) =>
        await registryProjects.Find(ContainerRegistryFilters.HarborProjectByTenant(tenantId))
            .FirstOrDefaultAsync(cancellationToken);
}
