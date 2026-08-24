using Pole.ResultType;

namespace Pole.Features.ContainerRegistries.Contracts;

public sealed record RegistryProjectProvisioningRequest(
    string TenantId,
    string ProjectName,
    string AttemptId);

public sealed record RegistryRobotNames(string Build, string Pull)
{
    public static RegistryRobotNames ForAttempt(string attemptId)
    {
        var normalized = attemptId.Trim().ToLowerInvariant();
        if (normalized.Length != 32 || normalized.Any(character => !char.IsAsciiHexDigit(character)))
            throw new ArgumentException("Registry provisioning attempt ids must be 32 hexadecimal characters.", nameof(attemptId));

        return new RegistryRobotNames(
            $"pole-build-{normalized}",
            $"pole-runtime-{normalized}");
    }
}

public sealed record RegistryRobotCleanupTarget(
    string ProjectName,
    string Name,
    string? ExternalRobotId);

public sealed record RegistryRobotCleanupResult(int MatchedByNameCount);

public sealed record RegistryRobotCredential(
    string ExternalRobotId,
    string Username,
    string Secret,
    DateTime? ExpiresAtUtc)
{
    public override string ToString() => "RegistryRobotCredential([REDACTED])";
}

public sealed record ProvisionedRegistryProject(
    string ExternalProjectId,
    string ProjectName,
    RegistryRobotCredential BuildCredential,
    RegistryRobotCredential PullCredential);

public sealed record RegistryPushTarget(
    string RegistryHost,
    string ProjectName,
    string RepositoryName,
    string Tag)
{
    public string Reference => $"{RegistryHost}/{ProjectName}/{RepositoryName}:{Tag}";
}

public sealed record RegistryArtifact(
    string RepositoryName,
    string Tag,
    string Digest,
    long SizeBytes);

public sealed record ContainerRegistryProviderNotConfigured(string Provider);

public sealed record ContainerRegistryProviderUnavailable(string Provider);

public union ContainerRegistryProviderError(
    ContainerRegistryProviderNotConfigured,
    ContainerRegistryProviderUnavailable);

public sealed record ContainerRegistryArtifactNotFound(string Reference);

public union ContainerRegistryArtifactLookupError(
    ContainerRegistryArtifactNotFound,
    ContainerRegistryProviderError);

public sealed record TenantContainerRegistryNotReady(string TenantId);

public sealed record TenantContainerRegistryProvisioningConflict(string TenantId);

public sealed record TenantContainerRegistryProjectNotFound(string RegistryProjectId);

public sealed record RegistryCredentialUnavailable(string RegistryProjectId);

public sealed record RegistryCredentialProtectionFailed;

public union EnsureTenantContainerRegistryProjectError(
    TenantContainerRegistryProvisioningConflict,
    ContainerRegistryProviderError,
    RegistryCredentialProtectionFailed);

public union PrepareRegistryBuildPushError(
    TenantContainerRegistryNotReady,
    TenantContainerRegistryProvisioningConflict,
    ContainerRegistryProviderError,
    RegistryCredentialUnavailable,
    RegistryCredentialProtectionFailed);

public union GetTenantRegistryArtifactError(
    TenantContainerRegistryProjectNotFound,
    TenantContainerRegistryNotReady,
    ContainerRegistryArtifactLookupError);

public union ResolveRuntimeRegistryAccessError(
    TenantContainerRegistryNotReady,
    RegistryCredentialUnavailable);

public sealed record RegistryBuildAccess(
    string RegistryProjectId,
    RegistryPushTarget Target,
    string KubernetesPullSecretName,
    string Username,
    string Secret)
{
    public override string ToString() =>
        $"RegistryBuildAccess(project={RegistryProjectId}, target={Target.Reference}, credentials=[REDACTED])";
}

public delegate Task<Result<RegistryBuildAccess, PrepareRegistryBuildPushError>>
    PrepareRegistryBuildPush(
        string tenantId,
        string initiatedByUserId,
        string repositoryName,
        string tag,
        CancellationToken cancellationToken = default);

public delegate Task<Result<RegistryArtifact, GetTenantRegistryArtifactError>>
    GetTenantRegistryArtifact(
        string tenantId,
        string registryProjectId,
        string repositoryName,
        string reference,
        CancellationToken cancellationToken = default);

public interface IContainerRegistryBuildService
{
    Task<Result<RegistryBuildAccess>> PreparePushAsync(
        string tenantId,
        string initiatedByUserId,
        string repositoryName,
        string tag,
        CancellationToken cancellationToken = default);

    Task<Result<RegistryArtifact>> GetArtifactAsync(
        string tenantId,
        string registryProjectId,
        string repositoryName,
        string reference,
        CancellationToken cancellationToken = default);
}

public interface IContainerRegistryClient
{
    Task<Result<ProvisionedRegistryProject, ContainerRegistryProviderError>> ProvisionProjectAsync(
        RegistryProjectProvisioningRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<RegistryArtifact, ContainerRegistryArtifactLookupError>> GetArtifactAsync(
        string projectName,
        string repositoryName,
        string reference,
        CancellationToken cancellationToken = default);

    Task<Result<RegistryRobotCleanupResult, ContainerRegistryProviderError>> DeleteRobotCredentialsAsync(
        IReadOnlyCollection<RegistryRobotCleanupTarget> targets,
        CancellationToken cancellationToken = default);

    Task<Result<Unit, ContainerRegistryProviderError>> DeleteProjectAsync(
        string externalProjectId,
        CancellationToken cancellationToken = default);
}

public sealed record RuntimeRegistryAccess(
    string RegistryProjectId,
    string RegistryHost,
    string ProjectName,
    string KubernetesSecretName,
    string Username,
    string Secret)
{
    public override string ToString() => "RuntimeRegistryAccess([REDACTED])";
}

public delegate Task<Result<RuntimeRegistryAccess, ResolveRuntimeRegistryAccessError>>
    ResolveRuntimeRegistryAccess(
        string tenantId,
        CancellationToken cancellationToken = default);
