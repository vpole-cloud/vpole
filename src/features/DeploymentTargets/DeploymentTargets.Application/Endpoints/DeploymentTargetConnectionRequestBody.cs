namespace Pole.Features.DeploymentTargets.Application.Endpoints;

public sealed record DeploymentTargetConnectionRequestBody(
    string Kind,
    string? Kubeconfig = null,
    string? ContextName = null,
    string? ServerUrl = null,
    string? CertificateAuthorityData = null,
    string? Token = null)
{
    public override string ToString() =>
        $"DeploymentTargetConnectionRequestBody(Kind={Kind}, Credential=[REDACTED])";
}
