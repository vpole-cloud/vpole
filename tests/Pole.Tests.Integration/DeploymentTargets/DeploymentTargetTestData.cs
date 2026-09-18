using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Endpoints;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Tests.Integration.DeploymentTargets;

internal static class DeploymentTargetTestData
{
    public static DeploymentTarget AttestForTests(this DeploymentTarget target) => target with
    {
        WorkloadConfiguration = target.WorkloadConfiguration with
        {
            NetworkPolicy = new DeploymentTargetNetworkPolicy(NetworkPolicyEnforcement.Supported,
                target.ClusterIdentity, target.Connection.Version, target.CreatedAtUtc, false)
        }
    };

    public static DeploymentTargetWorkloadConfiguration CreateWorkloadConfiguration(
        string ingressClassName = "traefik",
        string clusterIssuerName = "letsencrypt-prod") =>
        DeploymentTargetWorkloadConfiguration.Create(
            DeploymentTargetIngressConfiguration.Create(
                ingressClassName,
                clusterIssuerName));

    public static DeploymentTargetWorkloadConfigurationRequestBody CreateWorkloadConfigurationBody(
        string ingressClassName = "traefik",
        string clusterIssuerName = "letsencrypt-prod") =>
        new(
            new DeploymentTargetIngressConfigurationRequestBody(
                ingressClassName,
                clusterIssuerName));

    public static DeploymentTargetWorkloadConfigurationRequestBody ToRequestBody(
        this DeploymentTargetWorkloadConfigurationDto configuration) =>
        new(
            configuration.Ingress is { } ingress
                ? new DeploymentTargetIngressConfigurationRequestBody(
                    ingress.IngressClassName,
                    ingress.ClusterIssuerName)
                : null);

    public static DeploymentTargetWorkloadConfigurationRequestBody ToRequestBody(
        this DeploymentTargetWorkloadConfiguration configuration) =>
        new(
            configuration.Ingress is { } ingress
                ? new DeploymentTargetIngressConfigurationRequestBody(
                    ingress.IngressClassName,
                    ingress.ClusterIssuerName)
                : null);
}
