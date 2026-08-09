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
    Task<Result<ProvisionedRegistryProject>> ProvisionProjectAsync(
        RegistryProjectProvisioningRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<RegistryArtifact>> GetArtifactAsync(
        string projectName,
        string repositoryName,
        string reference,
        CancellationToken cancellationToken = default);

    Task<Result<RegistryRobotCleanupResult>> DeleteRobotCredentialsAsync(
        IReadOnlyCollection<RegistryRobotCleanupTarget> targets,
        CancellationToken cancellationToken = default);

    Task<Result> DeleteProjectAsync(
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

public interface IRuntimeRegistryAccessProvider
{
    Task<Result<RuntimeRegistryAccess>> ResolveAsync(
        string tenantId,
        CancellationToken cancellationToken = default);
}
