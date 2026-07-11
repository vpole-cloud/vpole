using Pole.ResultType;

namespace Pole.Features.WebServices.Contracts;

public sealed record SourceBuildDeploymentImage(
    string RegistryHost,
    string ImageName,
    string Tag,
    string Digest,
    string PullSecretName);

public sealed record SourceBuildDeploymentRequest(
    string TenantId,
    string WebServiceId,
    string BuildJobId,
    string SourceRepositoryId,
    string CommitSha,
    string? ActorUserId,
    string? ActorEmail,
    SourceBuildDeploymentImage Image);

public sealed record SourceBuildDeploymentResult(string ReleaseId, int Revision);

public interface IWebServiceSourceBuildDeploymentService
{
    Task<Result<SourceBuildDeploymentResult>> DeployAsync(
        SourceBuildDeploymentRequest request,
        CancellationToken cancellationToken = default);
}
