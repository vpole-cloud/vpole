using System.Net;
using k8s.Autorest;
using k8s.Models;
using Pole.Server.Features.RedisServices.Common;
using Pole.Server.Features.WebServices.Common;

namespace Pole.Server.Kuber;

public static class KubernetesExtensions
{
    public static async Task CreateIfNotExistAsync(Func<Task> createCall)
    {
        try
        {
            await createCall();
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.Conflict)
        {
            // create-only semantics
        }
    }
    extension(WebService webService)
    {
        public WebServiceInKubernetes ToKubernetes() => new(webService);
    }
    extension(Redis redis)
    {
        public RedisInKubernetes ToKubernetes() => new(redis);
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