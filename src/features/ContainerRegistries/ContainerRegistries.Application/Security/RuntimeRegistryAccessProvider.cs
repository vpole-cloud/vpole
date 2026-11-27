// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.ContainerRegistries.Application.Projects;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.ContainerRegistries.Domain;

namespace Pole.Features.ContainerRegistries.Application.Security;

public sealed class RuntimeRegistryAccessProvider(
    LoadTenantHarborRegistryProject loadTenantHarborRegistryProject,
    ResolveRegistryCredential resolveCredential,
    HarborOptions options
)
{
    public async Task<ResolveRuntimeRegistryAccessOutcome> ResolveAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var project = await loadTenantHarborRegistryProject(tenantId, cancellationToken);
        if (project?.State is not ReadyRegistryProject)
            return new ResolveRuntimeRegistryAccessError(new TenantContainerRegistryNotReady(tenantId));

        var credential = await resolveCredential(
            tenantId,
            project.Id,
            RegistryCredentialPurpose.RuntimePull,
            cancellationToken);
        if (credential.Value is RegistryCredentialUnavailable unavailable)
            return new ResolveRuntimeRegistryAccessError(unavailable);
        var resolved = (ResolvedRegistryCredential)credential.Value;

        return new RuntimeRegistryAccess(
            project.Id,
            options.RegistryHost,
            project.Name,
            RegistryKubernetesSecretNames.ForProject(project.Id),
            resolved.Username,
            resolved.Secret);
    }
}
