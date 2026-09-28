using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Endpoints;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Tests.Integration.DeploymentTargets;

internal static class DeploymentTargetTestData
{
    public static DeploymentTarget AttestForTests(this DeploymentTarget target) =>
        target.AttestNetworkPolicy(NetworkPolicyEnforcement.Supported, false, target.UpdatedAtUtc);

    public static DeploymentTargetWorkloadConfiguration CreateWorkloadConfiguration(
        string ingressClassName = "traefik",
        string clusterIssuerName = "letsencrypt-prod") =>
        DeploymentTargetWorkloadConfiguration.Create(
            DeploymentTargetIngressConfiguration.Create(
                ingressClassName,
                clusterIssuerName),
            DeploymentTargetNetworkSources.None);

    public static DeploymentTargetWorkloadConfigurationRequestBody CreateWorkloadConfigurationBody(
        string ingressClassName = "traefik",
        string clusterIssuerName = "letsencrypt-prod") =>
        new(
            new DeploymentTargetIngressConfigurationRequestBody(
                ingressClassName,
                clusterIssuerName),
            new DeploymentTargetNetworkSourcesRequestBody([]));

    public static DeploymentTargetWorkloadConfigurationRequestBody ToRequestBody(
        this DeploymentTargetWorkloadConfigurationDto configuration) =>
        new(
            configuration.Ingress is { } ingress
                ? new DeploymentTargetIngressConfigurationRequestBody(
                    ingress.IngressClassName,
                    ingress.ClusterIssuerName)
                : null,
            configuration.NetworkSources.ToRequestBody());

    public static DeploymentTargetWorkloadConfigurationRequestBody ToRequestBody(
        this DeploymentTargetWorkloadConfiguration configuration) =>
        new(
            configuration.Ingress is { } ingress
                ? new DeploymentTargetIngressConfigurationRequestBody(
                    ingress.IngressClassName,
                    ingress.ClusterIssuerName)
                : null,
            configuration.NetworkSources.ToRequestBody());

    public static DeploymentTargetNetworkSourcesRequestBody ToRequestBody(
        this Pole.Features.DeploymentTargets.Contracts.DeploymentTargetNetworkSourcesDto sources) =>
        new(sources.Entries.ToDictionary(
            entry => entry.Key, entry => entry.Value.ToRequestBody(), StringComparer.Ordinal));

    public static DeploymentTargetNetworkSourcesRequestBody ToRequestBody(
        this DeploymentTargetNetworkSources sources) =>
        new(sources.Entries.ToDictionary(
            entry => entry.Key, entry => entry.Value.ToRequestBody(), StringComparer.Ordinal));

    private static DeploymentTargetNetworkSourceRequestBody ToRequestBody(
        this Pole.Features.DeploymentTargets.Contracts.DeploymentTargetNetworkSourceDto source) => source switch
        {
            Pole.Features.DeploymentTargets.Contracts.DeploymentTargetPodNetworkSourceDto pod =>
                new DeploymentTargetPodNetworkSourceRequestBody(
                pod.Namespace,
                pod.PodLabels.ToDictionary(label => label.Key, label => label.Value)),
            Pole.Features.DeploymentTargets.Contracts.DeploymentTargetCidrNetworkSourceDto cidr =>
                new DeploymentTargetCidrNetworkSourceRequestBody(cidr.Cidr)
        };

    private static DeploymentTargetNetworkSourceRequestBody ToRequestBody(
        this DeploymentTargetNetworkSource source) => source switch
        {
            DeploymentTargetPodNetworkSource pod =>
                new DeploymentTargetPodNetworkSourceRequestBody(
                pod.Namespace,
                pod.PodLabels.ToDictionary(label => label.Key, label => label.Value)),
            DeploymentTargetCidrNetworkSource cidr =>
                new DeploymentTargetCidrNetworkSourceRequestBody(cidr.Cidr)
        };
}
