using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.RedisServices.Application.Kubernetes;

public static class RedisInKubernetesExtensions
{
    extension(Redis redis)
    {
        public RedisInKubernetes ToKubernetes(
            string kubernetesNamespace,
            ResolvedStorageConfiguration storageConfiguration,
            ResolvedComputeConfiguration computeConfiguration) =>
            RedisInKubernetes.Create(
                redis,
                kubernetesNamespace,
                storageConfiguration,
                computeConfiguration);
    }
}
