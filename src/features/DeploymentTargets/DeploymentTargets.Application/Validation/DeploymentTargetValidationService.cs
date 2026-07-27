using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Validation;

public sealed class DeploymentTargetValidationService(
    IDeploymentTargetKubernetesClientFactory clientFactory,
    IDeploymentTargetProbe probe,
    TimeProvider timeProvider)
{
    public async Task<DeploymentTargetProbeObservation> ProbeStoredConnectionAsync(
        DeploymentTarget target,
        CancellationToken cancellationToken = default)
    {
        var clientResult = await clientFactory.CreateAsync(
            target.Id,
            target.Connection,
            cancellationToken);
        if (!clientResult.TryGetValue(out var client, out _))
        {
            return DegradedObservation(
                target,
                "StoredConnectionUnavailable",
                "The stored Kubernetes connection could not be loaded.");
        }

        DeploymentTargetProbeObservation observation;
        using (client)
        {
            observation = await probe.ProbeAsync(
                client,
                target.Connection.Version,
                target.Health.LastHealthyAtUtc,
                cancellationToken);
        }

        return observation.ClusterIdentity is not null &&
               !string.Equals(
                   target.ClusterIdentity,
                   observation.ClusterIdentity,
                   StringComparison.Ordinal)
            ? DegradedObservation(
                target,
                "ClusterIdentityMismatch",
                "The stored connection resolved to a different Kubernetes cluster.")
            : observation;
    }

    private DeploymentTargetProbeObservation DegradedObservation(
        DeploymentTarget target,
        string failureCode,
        string failureMessage) =>
        new(
            ClusterIdentity: null,
            DeploymentTargetHealth.Degraded(
                target.Connection.Version,
                DeploymentTargetClock.UtcNowAfter(
                    timeProvider,
                    target.Health.LastHealthyAtUtc),
                target.Health.LastHealthyAtUtc,
                failureCode,
                failureMessage),
            DeploymentTargetCapabilities.Unknown(target.Connection.Version));
}
