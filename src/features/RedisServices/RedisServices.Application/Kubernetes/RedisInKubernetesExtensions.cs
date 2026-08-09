using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.RedisServices.Application.Kubernetes;

public static class RedisInKubernetesExtensions
{
    extension(Redis redis)
    {
        public RedisInKubernetes ToKubernetes(
            string kubernetesNamespace,
            ResourceDeploymentStorageConfiguration storageConfiguration,
            ResourceDeploymentComputeConfiguration computeConfiguration) =>
            RedisInKubernetes.Create(
                redis,
                kubernetesNamespace,
                storageConfiguration,
                computeConfiguration);
    }
}
