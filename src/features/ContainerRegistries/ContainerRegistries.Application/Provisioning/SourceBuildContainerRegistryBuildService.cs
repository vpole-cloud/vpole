using Pole.Features.ContainerRegistries.Contracts;
using Pole.ResultType;

namespace Pole.Features.ContainerRegistries.Application.Provisioning;

/// <summary>
/// Temporary adapter for SourceBuilds. Its removal is tracked in the application-boundary
/// compatibility ledger and must coincide with the SourceBuilds feature migration.
/// </summary>
internal sealed class SourceBuildContainerRegistryBuildService(
    PrepareRegistryBuildPush preparePush,
    GetTenantRegistryArtifact getArtifact) : IContainerRegistryBuildService
{
    public async Task<Result<RegistryBuildAccess>> PreparePushAsync(
        string tenantId,
        string initiatedByUserId,
        string repositoryName,
        string tag,
        CancellationToken cancellationToken = default)
    {
        var result = await preparePush(tenantId, initiatedByUserId, repositoryName, tag, cancellationToken);
        return result switch
        {
            Ok<RegistryBuildAccess> success => success.Value,
            Err<PrepareRegistryBuildPushError> error => Map(error.Value)
        };
    }

    public async Task<Result<RegistryArtifact>> GetArtifactAsync(
        string tenantId,
        string registryProjectId,
        string repositoryName,
        string reference,
        CancellationToken cancellationToken = default)
    {
        var result = await getArtifact(tenantId, registryProjectId, repositoryName, reference, cancellationToken);
        return result switch
        {
            Ok<RegistryArtifact> success => success.Value,
            Err<GetTenantRegistryArtifactError> error => Map(error.Value)
        };
    }

    private static Failure Map(PrepareRegistryBuildPushError error) => error switch
    {
        TenantContainerRegistryNotReady => Failures.Conflict("The tenant container registry is not ready."),
        TenantContainerRegistryProvisioningConflict => Failures.Conflict("The tenant container registry is being provisioned."),
        ContainerRegistryProviderError => Failures.ExternalService("Harbor could not complete the registry request.", "Harbor", "Harbor is unavailable or not configured."),
        RegistryCredentialUnavailable => Failures.NotFound("The container registry credential was not found."),
        RegistryCredentialProtectionFailed => Failures.Internal("The container registry credential could not be protected.", "Retry the provisioning operation.")
    };

    private static Failure Map(GetTenantRegistryArtifactError error) => error switch
    {
        TenantContainerRegistryProjectNotFound notFound => Failures.NotFound("The tenant container registry was not found.", notFound.RegistryProjectId),
        TenantContainerRegistryNotReady => Failures.Conflict("The tenant container registry is not ready."),
        ContainerRegistryArtifactLookupError lookup => lookup switch
        {
            ContainerRegistryArtifactNotFound notFound => Failures.NotFound("The container registry artifact was not found.", notFound.Reference),
            ContainerRegistryProviderError => Failures.ExternalService("Harbor could not complete the registry request.", "Harbor", "Harbor is unavailable or not configured.")
        }
    };
}
