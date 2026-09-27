using Pole.Features.ContainerRegistries.Application.Persistence;
using Pole.Features.ContainerRegistries.Application.Persistence.Projects;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.ContainerRegistries.Domain;

namespace Pole.Features.ContainerRegistries.Application.Provisioning;

public sealed class TenantRegistryArtifactLookup(
    LoadTenantHarborRegistryProject loadProject,
    IContainerRegistryClient registryClient)
{
    public async Task<GetTenantRegistryArtifactOutcome> InvokeAsync(
        string tenantId,
        string registryProjectId,
        string repositoryName,
        string reference,
        CancellationToken cancellationToken = default)
    {
        var project = await loadProject(tenantId, cancellationToken);
        if (project is null || project.Id != registryProjectId)
        {
            return new GetTenantRegistryArtifactError(
                new TenantContainerRegistryProjectNotFound(registryProjectId));
        }
        if (project.State is not ReadyRegistryProject)
        {
            return new GetTenantRegistryArtifactError(
                new TenantContainerRegistryNotReady(tenantId));
        }

        var artifact = await registryClient.GetArtifactAsync(
            project.Name, repositoryName, reference, cancellationToken);
        return artifact switch
        {
            RegistryArtifact success => success,
            ContainerRegistryArtifactLookupError error => new GetTenantRegistryArtifactError(error)
        };
    }
}
