namespace Pole.Features.DeploymentTargets.Contracts;

/// <summary>Broker-facing identity derived from an exact client-certificate pin.</summary>
public sealed record DeploymentTargetConnectorIdentity(
    string TargetId,
    string ConnectorId,
    long Generation,
    long Revision);

public sealed record DeploymentTargetConnectorRejected;

public union ResolveDeploymentTargetConnectorOutcome(
    DeploymentTargetConnectorIdentity,
    DeploymentTargetConnectorRejected);

/// <summary>
/// The caller must first prove possession of the presented certificate during the TLS handshake.
/// Certificate subject and SAN names are never authority for the target identity.
/// </summary>
public delegate Task<ResolveDeploymentTargetConnectorOutcome> ResolveDeploymentTargetConnector(
    string certificateFingerprintSha256,
    CancellationToken cancellationToken = default);

public sealed record DeploymentTargetConnectorContact(
    string TargetId,
    string ConnectorId,
    long Generation,
    string Version,
    string Health,
    IReadOnlyList<string> Capabilities,
    DateTime ObservedAtUtc);

public sealed record DeploymentTargetConnectorContactRecorded;

public union RecordDeploymentTargetConnectorContactOutcome(
    DeploymentTargetConnectorContactRecorded,
    DeploymentTargetConnectorRejected);

public delegate Task<RecordDeploymentTargetConnectorContactOutcome> RecordDeploymentTargetConnectorContact(
    DeploymentTargetConnectorContact contact,
    CancellationToken cancellationToken = default);
