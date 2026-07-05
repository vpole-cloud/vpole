using Pole.Features.RedisServices.Domain;

namespace Pole.Features.RedisServices.Application.Kubernetes;

public static class RedisInKubernetesExtensions
{
    extension(Redis redis)
    {
        public RedisInKubernetes ToKubernetes() => new(redis);
    }
}
