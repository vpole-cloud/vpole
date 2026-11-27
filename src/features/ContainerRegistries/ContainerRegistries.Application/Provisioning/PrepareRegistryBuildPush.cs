// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.ContainerRegistries.Application.Security;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.ContainerRegistries.Domain;

namespace Pole.Features.ContainerRegistries.Application.Provisioning;

internal sealed class ContainerRegistryBuildPushPreparer(
    EnsureTenantContainerRegistryProject ensureProject,
    ResolveRegistryCredential resolveCredential,
    HarborOptions options
)
{
    public async Task<PrepareRegistryBuildPushOutcome> InvokeAsync(
        PrepareRegistryBuildPushRequest request,
        CancellationToken cancellationToken = default)
    {
        var projectResult = await ensureProject(request.TenantId, request.InitiatedByUserId, cancellationToken);
        if (projectResult is TenantContainerRegistryProvisioningConflict conflict)
            return new PrepareRegistryBuildPushError(conflict);
        if (projectResult is ContainerRegistryProviderError provider)
            return new PrepareRegistryBuildPushError(provider);
        if (projectResult is RegistryCredentialProtectionFailed failed)
            return new PrepareRegistryBuildPushError(failed);
        var project = (RegistryProject)projectResult.Value;
        if (project.State is not ReadyRegistryProject)
            return new PrepareRegistryBuildPushError(new TenantContainerRegistryNotReady(request.TenantId));

        var credential = await resolveCredential(
            request.TenantId,
            project.Id,
            RegistryCredentialPurpose.BuildPushPull,
            cancellationToken);
        if (credential.Value is RegistryCredentialUnavailable unavailable)
            return new PrepareRegistryBuildPushError(unavailable);
        var resolved = (ResolvedRegistryCredential)credential.Value;
        return new RegistryBuildAccess(
            project.Id,
            new RegistryPushTarget(options.RegistryHost, project.Name, request.RepositoryName, request.Tag),
            RegistryKubernetesSecretNames.ForProject(project.Id),
            resolved.Username,
            resolved.Secret);
    }
}
