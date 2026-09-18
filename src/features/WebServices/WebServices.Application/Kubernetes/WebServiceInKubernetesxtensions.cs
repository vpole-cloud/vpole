using Pole.Features.Core.Kubernetes;
using k8s.Models;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Kubernetes;

public static class WebServiceInKubernetesxtensions
{
    extension(WebService webService)
    {
        public WebServiceInKubernetes ToKubernetes(
            string kubernetesNamespace,
            ResourceDeploymentIngressConfiguration ingressConfiguration,
            ResourceDeploymentComputeConfiguration computeConfiguration,
            int fixedReplicas, WorkloadIdentityLabels placementIdentity) =>
            WebServiceInKubernetes.Create(
                webService,
                kubernetesNamespace,
                ingressConfiguration,
                computeConfiguration,
                fixedReplicas, placementIdentity);
    }
    extension(ProbeConfig probeConfig)
    {
        public V1Probe ToV1Probe() => new()
        {
            HttpGet = new V1HTTPGetAction
            {
                Path = probeConfig.Path,
                Port = probeConfig.Port
            },
            InitialDelaySeconds = probeConfig.InitialDelaySeconds,
            PeriodSeconds = probeConfig.PeriodSeconds,
            TimeoutSeconds = probeConfig.TimeoutSeconds,
            FailureThreshold = probeConfig.FailureThreshold,
            SuccessThreshold = probeConfig.SuccessThreshold,
        };
    }
}
