using k8s;
using Pole.Features.Core.Kubernetes;

namespace Pole.Features.RedisServices.Kubernetes;

public static class RedisKubernetesResourceManager
{
    public static async Task CreateAsync(
        IKubernetes kubernetes,
        RedisInKubernetes redis,
        CancellationToken cancellationToken = default)
    {
        var statefulSet = redis.ToStatefulSet();
        var service = redis.ToService();

        await KubernetesExtensions.CreateIfNotExistAsync(() =>
            kubernetes.AppsV1.CreateNamespacedStatefulSetAsync(
                statefulSet,
                redis.Namespace,
                cancellationToken: cancellationToken));

        await KubernetesExtensions.CreateIfNotExistAsync(() =>
            kubernetes.CoreV1.CreateNamespacedServiceAsync(
                service,
                redis.Namespace,
                cancellationToken: cancellationToken));
    }

    public static async Task ReplaceAsync(
        IKubernetes kubernetes,
        RedisInKubernetes redis,
        CancellationToken cancellationToken = default)
    {
        var statefulSet = redis.ToStatefulSet();
        var service = redis.ToService();

        await kubernetes.AppsV1.ReplaceNamespacedStatefulSetAsync(
            statefulSet,
            statefulSet.Metadata.Name,
            redis.Namespace,
            cancellationToken: cancellationToken);

        await kubernetes.CoreV1.ReplaceNamespacedServiceAsync(
            service,
            service.Metadata.Name,
            redis.Namespace,
            cancellationToken: cancellationToken);
    }

    public static async Task DeleteAsync(
        IKubernetes kubernetes,
        string redisName,
        string @namespace,
        CancellationToken cancellationToken = default)
    {
        await KubernetesExtensions.DeleteIfExistsAsync(() =>
            kubernetes.AppsV1.DeleteNamespacedStatefulSetAsync(
                name: redisName,
                namespaceParameter: @namespace,
                cancellationToken: cancellationToken));

        await KubernetesExtensions.DeleteIfExistsAsync(() =>
            kubernetes.CoreV1.DeleteNamespacedServiceAsync(
                name: RedisInKubernetes.BuildServiceName(redisName),
                namespaceParameter: @namespace,
                cancellationToken: cancellationToken));
    }
}
