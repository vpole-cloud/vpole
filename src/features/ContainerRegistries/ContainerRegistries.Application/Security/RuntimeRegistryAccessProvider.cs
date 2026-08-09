using MongoDB.Driver;
using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.ContainerRegistries.Domain;
using Pole.ResultType;

namespace Pole.Features.ContainerRegistries.Application.Security;

public sealed class RuntimeRegistryAccessProvider(
    IContainerRegistryDbContext dbContext,
    IRegistryCredentialResolver credentialResolver,
    HarborOptions options) : IRuntimeRegistryAccessProvider
{
    public async Task<Result<RuntimeRegistryAccess>> ResolveAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var project = await dbContext.RegistryProjects
            .Find(item => item.TenantId == tenantId && item.Provider == ContainerRegistryProvider.Harbor)
            .FirstOrDefaultAsync(cancellationToken);
        if (project?.State is not ReadyRegistryProject)
            return Failures.Conflict("The tenant container registry is not ready.");

        var credential = await credentialResolver.ResolveAsync(
            tenantId,
            project.Id,
            RegistryCredentialPurpose.RuntimePull,
            cancellationToken);
        if (!credential.TryGetValue(out var resolved, out var failure))
            return failure;

        return new RuntimeRegistryAccess(
            project.Id,
            options.RegistryHost,
            project.Name,
            RegistryKubernetesSecretNames.ForProject(project.Id),
            resolved.Username,
            resolved.Secret);
    }
}
