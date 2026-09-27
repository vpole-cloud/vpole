using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Pole.Features.Core.Kubernetes;

namespace Pole.Features.WebServices.Application.Kubernetes;

public sealed record WebServiceKubernetesApplyFailed(string Message);
public sealed record WebServiceKubernetesApplied;

public union WebServiceKubernetesApplyOutcome(
    WebServiceKubernetesApplied,
    WebServiceKubernetesApplyFailed);

public static class WebServiceKubernetesResourceManager
{
    public static async Task ApplyImagePullSecretAsync(
        IKubernetes kubernetes,
        WebServiceInKubernetes webService,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var secret = webService.ToImagePullSecret(username, password);
        if (secret is null)
            return;

        await CreateOrReplaceAsync(
            () => kubernetes.CoreV1.CreateNamespacedSecretAsync(
                secret,
                webService.Namespace,
                cancellationToken: cancellationToken),
            async () =>
            {
                var existing = await kubernetes.CoreV1.ReadNamespacedSecretAsync(
                    secret.Metadata.Name,
                    webService.Namespace,
                    cancellationToken: cancellationToken);
                secret.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                await kubernetes.CoreV1.ReplaceNamespacedSecretAsync(
                    secret,
                    secret.Metadata.Name,
                    webService.Namespace,
                    cancellationToken: cancellationToken);
            });
    }

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

    public static async Task<WebServiceKubernetesApplyOutcome> ApplyAsync(
        IKubernetes kubernetes,
        WebServiceInKubernetes webService,
        WebServiceDeploymentMetadata deploymentMetadata,
        CancellationToken cancellationToken = default)
    {
        var stage = "preparing the deployment manifest";
        try
        {
            stage = "preparing the ConfigMap manifest";
            var configMap = webService.ToConfigMap();

            stage = "preparing the Deployment manifest";
            var deployment = webService.ToDeployment(deploymentMetadata);

            stage = "preparing the Service manifest";
            var service = webService.ToService();

            stage = "preparing the Ingress manifest";
            var ingress = webService.ToIngress();

            stage = "preparing the ServiceMonitor manifest";
            var serviceMonitor = webService.ToServiceMonitor();

            stage = "applying the ConfigMap";
            await CreateOrReplaceAsync(
                () => kubernetes.CoreV1.CreateNamespacedConfigMapAsync(
                    configMap,
                    webService.Namespace,
                    cancellationToken: cancellationToken),
                async () =>
                {
                    var existing = await kubernetes.CoreV1.ReadNamespacedConfigMapAsync(
                        configMap.Metadata.Name,
                        webService.Namespace,
                        cancellationToken: cancellationToken);
                    configMap.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                    await kubernetes.CoreV1.ReplaceNamespacedConfigMapAsync(
                        configMap,
                        configMap.Metadata.Name,
                        webService.Namespace,
                        cancellationToken: cancellationToken);
                });
            stage = "applying the Deployment";
            await CreateOrReplaceAsync(
                () => kubernetes.AppsV1.CreateNamespacedDeploymentAsync(
                    deployment,
                    webService.Namespace,
                    cancellationToken: cancellationToken),
                async () =>
                {
                    var existing = await kubernetes.AppsV1.ReadNamespacedDeploymentAsync(
                        deployment.Metadata.Name,
                        webService.Namespace,
                        cancellationToken: cancellationToken);
                    deployment.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                    await kubernetes.AppsV1.ReplaceNamespacedDeploymentAsync(
                        deployment,
                        deployment.Metadata.Name,
                        webService.Namespace,
                        cancellationToken: cancellationToken);
                });
            stage = "applying the Service";
            await CreateOrReplaceAsync(
                () => kubernetes.CoreV1.CreateNamespacedServiceAsync(
                    service,
                    webService.Namespace,
                    cancellationToken: cancellationToken),
                async () =>
                {
                    var existing = await kubernetes.CoreV1.ReadNamespacedServiceAsync(
                        service.Metadata.Name,
                        webService.Namespace,
                        cancellationToken: cancellationToken);
                    PreserveServiceOwnedFields(service, existing);
                    await kubernetes.CoreV1.ReplaceNamespacedServiceAsync(
                        service,
                        service.Metadata.Name,
                        webService.Namespace,
                        cancellationToken: cancellationToken);
                });
            stage = "applying the Ingress";
            await CreateOrReplaceAsync(
                () => kubernetes.NetworkingV1.CreateNamespacedIngressAsync(
                    ingress,
                    webService.Namespace,
                    cancellationToken: cancellationToken),
                async () =>
                {
                    var existing = await kubernetes.NetworkingV1.ReadNamespacedIngressAsync(
                        ingress.Metadata.Name,
                        webService.Namespace,
                        cancellationToken: cancellationToken);
                    ingress.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                    await kubernetes.NetworkingV1.ReplaceNamespacedIngressAsync(
                        ingress,
                        ingress.Metadata.Name,
                        webService.Namespace,
                        cancellationToken: cancellationToken);
                });
            var serviceMonitorObject = ToJsonObject(serviceMonitor);

            stage = "applying the ServiceMonitor";
            await CreateOrReplaceAsync(
                () => kubernetes.CustomObjects.CreateNamespacedCustomObjectAsync(
                    serviceMonitorObject,
                    group: "monitoring.coreos.com",
                    version: "v1",
                    namespaceParameter: webService.Namespace,
                    plural: "servicemonitors",
                    cancellationToken: cancellationToken),
                async () =>
                {
                    var existing = await kubernetes.CustomObjects.GetNamespacedCustomObjectAsync(
                        group: "monitoring.coreos.com",
                        version: "v1",
                        namespaceParameter: webService.Namespace,
                        plural: "servicemonitors",
                        name: webService.ServiceMonitorName,
                        cancellationToken: cancellationToken);
                    var desired = ToJsonObject(serviceMonitor);
                    CopyResourceVersion(existing, desired);

                    await kubernetes.CustomObjects.ReplaceNamespacedCustomObjectAsync(
                        desired,
                        group: "monitoring.coreos.com",
                        version: "v1",
                        namespaceParameter: webService.Namespace,
                        plural: "servicemonitors",
                        name: webService.ServiceMonitorName,
                        cancellationToken: cancellationToken);

                });

            return new WebServiceKubernetesApplied();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new WebServiceKubernetesApplyFailed(
                $"Unable to apply web service deployment in Kubernetes while {stage}.");
        }
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

    private static async Task CreateOrReplaceAsync(Func<Task> createCall, Func<Task> replaceCall)
    {
        try
        {
            await createCall();
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.Conflict)
        {
            await replaceCall();
        }
    }

    private static void PreserveServiceOwnedFields(V1Service desired, V1Service existing)
    {
        desired.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
        desired.Spec.ClusterIP = existing.Spec.ClusterIP;
        desired.Spec.ClusterIPs = existing.Spec.ClusterIPs;
        desired.Spec.HealthCheckNodePort = existing.Spec.HealthCheckNodePort;
    }

    private static JsonObject ToJsonObject(object value)
    {
        var node = JsonSerializer.SerializeToNode(value);
        return node is JsonObject jsonObject
            ? jsonObject
            : throw new InvalidOperationException(
                "Kubernetes custom object serialization produced an empty object.");
    }

    private static void CopyResourceVersion(object existing, JsonObject desired)
    {
        var existingJson = ToJsonObject(existing);

        var existingMetadata = existingJson["metadata"]?.AsObject();
        var resourceVersion = existingMetadata?["resourceVersion"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(resourceVersion))
        {
            throw new InvalidOperationException(
                "Existing Kubernetes custom object does not include metadata.resourceVersion.");
        }

        var desiredMetadata = desired["metadata"]?.AsObject();
        if (desiredMetadata is null)
        {
            desiredMetadata = [];
            desired["metadata"] = desiredMetadata;
        }

        desiredMetadata["resourceVersion"] = resourceVersion;
    }
}
