using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.ContainerRegistries.Application.Security;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.ContainerRegistries.Domain;
using Pole.ResultType;

namespace Pole.Features.ContainerRegistries.Application.Provisioning;

public sealed class ContainerRegistryBuildService(
    IContainerRegistryDbContext dbContext,
    IContainerRegistryProvisioningService provisioningService,
    IRegistryCredentialResolver credentialResolver,
    IContainerRegistryClient registryClient,
    HarborOptions options) : IContainerRegistryBuildService
{
    public async Task<Result<RegistryBuildAccess>> PreparePushAsync(
        string tenantId,
        string initiatedByUserId,
        string repositoryName,
        string tag,
        CancellationToken cancellationToken = default)
    {
        var projectResult = await provisioningService.EnsureTenantProjectAsync(
            tenantId,
            initiatedByUserId,
            cancellationToken);
        if (!projectResult.TryGetValue(out var project, out var projectFailure))
            return projectFailure;
        if (project.State is not ReadyRegistryProject)
            return Failures.Conflict("The tenant container registry is not ready.");

        var credentialResult = await credentialResolver.ResolveAsync(
            tenantId,
            project.Id,
            RegistryCredentialPurpose.BuildPushPull,
            cancellationToken);
        if (!credentialResult.TryGetValue(out var credential, out var credentialFailure))
            return credentialFailure;

        return new RegistryBuildAccess(
            project.Id,
            new RegistryPushTarget(
                options.RegistryHost,
                project.Name,
                repositoryName,
                tag),
            RegistryKubernetesSecretNames.ForProject(project.Id),
            credential.Username,
            credential.Secret);
    }

    public async Task<Result<RegistryArtifact>> GetArtifactAsync(
        string tenantId,
        string registryProjectId,
        string repositoryName,
        string reference,
        CancellationToken cancellationToken = default)
    {
        var project = await dbContext.RegistryProjects
            .Find(item =>
                item.TenantId == tenantId &&
                item.Id == registryProjectId &&
                item.Provider == ContainerRegistryProvider.Harbor)
            .FirstOrDefaultAsync(cancellationToken);
        if (project is null)
            return Failures.NotFound("The tenant container registry was not found.", registryProjectId);
        if (project.State is not ReadyRegistryProject)
            return Failures.Conflict("The tenant container registry is not ready.");

        return await registryClient.GetArtifactAsync(
            project.Name,
            repositoryName,
            reference,
            cancellationToken);
    }
}

public static class ContainerRegistryCapabilityRegistration
{
    public static IServiceCollection AddContainerRegistryCapabilities(this IServiceCollection services)
    {
        services.TryAddScoped<IContainerRegistryBuildService, ContainerRegistryBuildService>();
        return services;
    }
}
