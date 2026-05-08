using Pole.Domain.Models;

namespace Pole.Features.RedisServices.Kubernetes;

public static class RedisInKubernetesExtensions
{
    extension(Redis redis)
    {
        public RedisInKubernetes ToKubernetes() => new(redis);
    }
}