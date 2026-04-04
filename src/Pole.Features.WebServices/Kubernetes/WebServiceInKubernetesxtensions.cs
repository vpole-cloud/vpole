using k8s.Models;
using Pole.Domain.Models;

namespace Pole.Features.WebServices.Kubernetes;

public static class WebServiceInKubernetesxtensions
{
    extension(WebService webService)
    {
        public WebServiceInKubernetes ToKubernetes() => new(webService);
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