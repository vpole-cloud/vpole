using k8s;
using Pole.Features.DeploymentTargets.Application.Validation;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Tests.Integration.Infrastructure;

internal sealed class TestDeploymentTargetProbe
{
    public Task<DeploymentTargetProbeObservation> InvokeAsync(
        IKubernetes kubernetes,
        long connectionVersion,
        DateTime? lastHealthyAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTime.UtcNow;
        var capabilities = DeploymentTargetCapabilities.Observed(
            connectionVersion,
            now,
            "v1.35.0-test",
            new DeploymentTargetNamespaceCapabilities(true, true, true, true, true),
            new DeploymentTargetNetworkPolicyCapabilities(true, true, true, true, true));
        var identity = $"test:kubernetes:{kubernetes.BaseUri.Host}:{kubernetes.BaseUri.Port}";
        return Task.FromResult(new DeploymentTargetProbeObservation(
            identity,
            DeploymentTargetHealth.Healthy(connectionVersion, now),
            capabilities));
    }
}
