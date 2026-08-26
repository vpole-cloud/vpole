using Pole.Features.ContainerRegistries.Application.Persistence;
using Pole.Features.ContainerRegistries.Application.Persistence.Projects;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.ContainerRegistries.Domain;
using Pole.ResultType;

namespace Pole.Features.ContainerRegistries.Application.Provisioning;

public sealed class TenantRegistryArtifactLookup(
    LoadTenantHarborRegistryProject loadProject,
    IContainerRegistryClient registryClient)
{
    public async Task<Result<RegistryArtifact, GetTenantRegistryArtifactError>> InvokeAsync(
        string tenantId,
        string registryProjectId,
        string repositoryName,
        string reference,
        CancellationToken cancellationToken = default)
    {
        var project = await loadProject(tenantId, cancellationToken);
        if (project is null || project.Id != registryProjectId)
        {
            return new Err<GetTenantRegistryArtifactError>(
                new(new TenantContainerRegistryProjectNotFound(registryProjectId)));
        }
        if (project.State is not ReadyRegistryProject)
        {
            return new Err<GetTenantRegistryArtifactError>(
                new(new TenantContainerRegistryNotReady(tenantId)));
        }

        var artifact = await registryClient.GetArtifactAsync(
            project.Name, repositoryName, reference, cancellationToken);
        return artifact switch
        {
            Ok<RegistryArtifact> success => new Ok<RegistryArtifact>(success.Value),
            Err<ContainerRegistryArtifactLookupError> error =>
                new Err<GetTenantRegistryArtifactError>(new(error.Value))
        };
    }
}
