// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.ContainerRegistries.Application.Projects;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.ContainerRegistries.Domain;

namespace Pole.Features.ContainerRegistries.Application.Provisioning;

public sealed class TenantRegistryArtifactLookup(
    LoadTenantHarborRegistryProject loadProject,
    IContainerRegistryClient registryClient
)
{
    public async Task<GetTenantRegistryArtifactOutcome> InvokeAsync(
        GetTenantRegistryArtifactRequest request,
        CancellationToken cancellationToken = default)
    {
        var project = await loadProject(request.TenantId, cancellationToken);
        if (project is null || project.Id != request.RegistryProjectId)
            return new GetTenantRegistryArtifactError(
                new TenantContainerRegistryProjectNotFound(request.RegistryProjectId));
        if (project.State is not ReadyRegistryProject)
            return new GetTenantRegistryArtifactError(new TenantContainerRegistryNotReady(request.TenantId));

        var artifact = await registryClient.GetArtifactAsync(
            new GetRegistryArtifactRequest(
                ProjectName: project.Name,
                RepositoryName: request.RepositoryName,
                Reference: request.Reference),
            cancellationToken: cancellationToken);
        return artifact switch
        {
            RegistryArtifact success => success,
            ContainerRegistryArtifactLookupError error => new GetTenantRegistryArtifactError(error),
        };
    }
}
