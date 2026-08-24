using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.ContainerRegistries.Application.Security;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.ContainerRegistries.Domain;
using Pole.ResultType;

namespace Pole.Features.ContainerRegistries.Application.Provisioning;

internal sealed class ContainerRegistryBuildPushPreparer(
    EnsureTenantContainerRegistryProject ensureProject,
    ResolveRegistryCredential resolveCredential,
    HarborOptions options)
{
    public async Task<Result<RegistryBuildAccess, PrepareRegistryBuildPushError>> InvokeAsync(
        string tenantId,
        string initiatedByUserId,
        string repositoryName,
        string tag,
        CancellationToken cancellationToken = default)
    {
        var projectResult = await ensureProject(tenantId, initiatedByUserId, cancellationToken);
        var ensuredProject = TypedResultExtensions.TryGetValue(projectResult);
        if (!ensuredProject.Succeeded)
            return new Err<PrepareRegistryBuildPushError>(MapProjectError(ensuredProject.Error!));
        var project = ensuredProject.Value!;
        if (project.State is not ReadyRegistryProject)
            return new Err<PrepareRegistryBuildPushError>(new(new TenantContainerRegistryNotReady(tenantId)));

        var credential = await resolveCredential(
            tenantId,
            project.Id,
            RegistryCredentialPurpose.BuildPushPull,
            cancellationToken);
        var resolvedCredential = TypedResultExtensions.TryGetValue(credential);
        if (!resolvedCredential.Succeeded)
            return new Err<PrepareRegistryBuildPushError>(new(resolvedCredential.Error!));
        var resolved = resolvedCredential.Value!;
        return new Ok<RegistryBuildAccess>(new(
            project.Id,
            new RegistryPushTarget(options.RegistryHost, project.Name, repositoryName, tag),
            RegistryKubernetesSecretNames.ForProject(project.Id),
            resolved.Username,
            resolved.Secret));
    }

    private static PrepareRegistryBuildPushError MapProjectError(
        EnsureTenantContainerRegistryProjectError error) =>
        error switch
        {
            TenantContainerRegistryProvisioningConflict conflict => new(conflict),
            ContainerRegistryProviderError provider => new(provider),
            RegistryCredentialProtectionFailed protectionFailed => new(protectionFailed)
        };
}
