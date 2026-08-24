using Pole.Features.ContainerRegistries.Application.Persistence;
using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.ContainerRegistries.Domain;
using Pole.ResultType;

namespace Pole.Features.ContainerRegistries.Application.Security;

public sealed class RuntimeRegistryAccessProvider(
    LoadTenantHarborRegistryProject loadTenantHarborRegistryProject,
    ResolveRegistryCredential resolveCredential,
    HarborOptions options)
{
    public async Task<Result<RuntimeRegistryAccess, ResolveRuntimeRegistryAccessError>> ResolveAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var project = await loadTenantHarborRegistryProject(tenantId, cancellationToken);
        if (project?.State is not ReadyRegistryProject)
            return new Err<ResolveRuntimeRegistryAccessError>(
                new(new TenantContainerRegistryNotReady(tenantId)));

        var credential = await resolveCredential(
            tenantId,
            project.Id,
            RegistryCredentialPurpose.RuntimePull,
            cancellationToken);
        var resolvedCredential = TypedResultExtensions.TryGetValue(credential);
        if (!resolvedCredential.Succeeded)
            return new Err<ResolveRuntimeRegistryAccessError>(new(resolvedCredential.Error!));
        var resolved = resolvedCredential.Value!;

        return new Ok<RuntimeRegistryAccess>(new RuntimeRegistryAccess(
            project.Id,
            options.RegistryHost,
            project.Name,
            RegistryKubernetesSecretNames.ForProject(project.Id),
            resolved.Username,
            resolved.Secret));
    }
}
