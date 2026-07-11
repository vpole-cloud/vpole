using Pole.ResultType;

namespace Pole.Features.ContainerRegistries.Contracts;

public sealed record RegistryProjectProvisioningRequest(string TenantId, string ProjectName);

public sealed record RegistryRobotCredential(
    string ExternalRobotId,
    string Username,
    string Secret,
    DateTime? ExpiresAtUtc);

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
    string Secret);

public interface IRuntimeRegistryAccessProvider
{
    Task<Result<RuntimeRegistryAccess>> ResolveAsync(
        string tenantId,
        CancellationToken cancellationToken = default);
}
