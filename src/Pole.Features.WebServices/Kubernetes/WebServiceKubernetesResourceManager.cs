using k8s;
using Pole.Features.Core.Kubernetes;

namespace Pole.Features.WebServices.Kubernetes;

public static class WebServiceKubernetesResourceManager
{
    public static async Task CreateAsync(
        IKubernetes kubernetes,
        WebServiceInKubernetes webService,
        CancellationToken cancellationToken = default)
    {
        var configMap = webService.ToConfigMap();
        var deployment = webService.ToDeployment();
        var service = webService.ToService();
        var ingress = webService.ToIngress();
        var serviceMonitor = webService.ToServiceMonitor();

        await KubernetesExtensions.CreateIfNotExistAsync(() =>
            kubernetes.CoreV1.CreateNamespacedConfigMapAsync(configMap, webService.Namespace, cancellationToken: cancellationToken));

        await KubernetesExtensions.CreateIfNotExistAsync(() =>
            kubernetes.AppsV1.CreateNamespacedDeploymentAsync(deployment, webService.Namespace, cancellationToken: cancellationToken));

        await KubernetesExtensions.CreateIfNotExistAsync(() =>
            kubernetes.CoreV1.CreateNamespacedServiceAsync(service, webService.Namespace, cancellationToken: cancellationToken));

        await KubernetesExtensions.CreateIfNotExistAsync(() =>
            kubernetes.NetworkingV1.CreateNamespacedIngressAsync(ingress, webService.Namespace, cancellationToken: cancellationToken));

        await KubernetesExtensions.CreateIfNotExistAsync(() =>
            kubernetes.CustomObjects.CreateNamespacedCustomObjectAsync(
                serviceMonitor,
                group: "monitoring.coreos.com",
                version: "v1",
                namespaceParameter: webService.Namespace,
                plural: "servicemonitors",
                cancellationToken: cancellationToken));
    }

    public static async Task ReplaceAsync(
        IKubernetes kubernetes,
        WebServiceInKubernetes webService,
        CancellationToken cancellationToken = default)
    {
        var configMap = webService.ToConfigMap();
        var deployment = webService.ToDeployment();
        var service = webService.ToService();
        var ingress = webService.ToIngress();

        await kubernetes.CoreV1.ReplaceNamespacedConfigMapAsync(
            configMap,
            configMap.Metadata.Name,
            webService.Namespace,
            cancellationToken: cancellationToken);

        await kubernetes.AppsV1.ReplaceNamespacedDeploymentAsync(
            deployment,
            deployment.Metadata.Name,
            webService.Namespace,
            cancellationToken: cancellationToken);

        await kubernetes.CoreV1.ReplaceNamespacedServiceAsync(
            service,
            service.Metadata.Name,
            webService.Namespace,
            cancellationToken: cancellationToken);

        await kubernetes.NetworkingV1.ReplaceNamespacedIngressAsync(
            ingress,
            ingress.Metadata.Name,
            webService.Namespace,
            cancellationToken: cancellationToken);
    }

    public static async Task DeleteAsync(
        IKubernetes kubernetes,
        string webServiceName,
        string @namespace,
        CancellationToken cancellationToken = default)
    {
        await KubernetesExtensions.DeleteIfExistsAsync(() =>
            kubernetes.CustomObjects.DeleteNamespacedCustomObjectAsync(
                group: "monitoring.coreos.com",
                version: "v1",
                namespaceParameter: @namespace,
                plural: "servicemonitors",
                name: WebServiceInKubernetes.BuildServiceMonitorName(webServiceName),
                cancellationToken: cancellationToken));

        await KubernetesExtensions.DeleteIfExistsAsync(() =>
            kubernetes.NetworkingV1.DeleteNamespacedIngressAsync(
                name: WebServiceInKubernetes.BuildIngressName(webServiceName),
                namespaceParameter: @namespace,
                cancellationToken: cancellationToken));

        await KubernetesExtensions.DeleteIfExistsAsync(() =>
            kubernetes.CoreV1.DeleteNamespacedServiceAsync(
                name: WebServiceInKubernetes.BuildServiceName(webServiceName),
                namespaceParameter: @namespace,
                cancellationToken: cancellationToken));

        await KubernetesExtensions.DeleteIfExistsAsync(() =>
            kubernetes.AppsV1.DeleteNamespacedDeploymentAsync(
                name: WebServiceInKubernetes.BuildDeploymentName(webServiceName),
                namespaceParameter: @namespace,
                cancellationToken: cancellationToken));

        await KubernetesExtensions.DeleteIfExistsAsync(() =>
            kubernetes.CoreV1.DeleteNamespacedConfigMapAsync(
                name: WebServiceInKubernetes.BuildConfigMapName(webServiceName),
                namespaceParameter: @namespace,
                cancellationToken: cancellationToken));

        await KubernetesExtensions.DeleteIfExistsAsync(() =>
            kubernetes.CoreV1.DeleteNamespacedSecretAsync(
                name: WebServiceInKubernetes.BuildTlsSecretName(webServiceName),
                namespaceParameter: @namespace,
                cancellationToken: cancellationToken));
    }
}
