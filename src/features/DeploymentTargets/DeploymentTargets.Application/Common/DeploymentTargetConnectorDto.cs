using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Common;

public sealed record DeploymentTargetConnectorDto(
    string Id,
    string TargetId,
    string CredentialStatus,
    long Generation,
    long Revision,
    DateTime CertificateNotAfterUtc,
    string? Version,
    string Health,
    IReadOnlyList<string> Capabilities,
    DateTime? LastContactAtUtc,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

internal static class DeploymentTargetConnectorDtoMapping
{
    public static DeploymentTargetConnectorDto ToDto(
        this DeploymentTargetConnector connector,
        DateTime nowUtc) => new(
        connector.Id,
        connector.TargetId,
        connector.State switch
        {
            RevokedDeploymentTargetConnectorState => "Revoked",
            ActiveDeploymentTargetConnectorState when connector.CertificateNotAfterUtc <= nowUtc => "Expired",
            ActiveDeploymentTargetConnectorState => "Active"
        },
        connector.Generation,
        connector.Revision,
        connector.CertificateNotAfterUtc,
        connector.Observation?.Version,
        connector.State switch
        {
            RevokedDeploymentTargetConnectorState => "Disconnected",
            ActiveDeploymentTargetConnectorState when connector.CertificateNotAfterUtc <= nowUtc => "Disconnected",
            ActiveDeploymentTargetConnectorState when connector.Observation is null => "Unknown",
            ActiveDeploymentTargetConnectorState when !connector.Observation.IsFreshAt(nowUtc) => "Disconnected",
            ActiveDeploymentTargetConnectorState => connector.Observation.Health
        },
        connector.Observation?.Capabilities.ToArray() ?? [],
        connector.Observation?.LastContactAtUtc,
        connector.CreatedAtUtc,
        connector.UpdatedAtUtc);
}
