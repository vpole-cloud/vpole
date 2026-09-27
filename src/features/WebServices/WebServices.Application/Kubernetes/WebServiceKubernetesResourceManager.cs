using System.Net;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Pole.Features.Core.Kubernetes;

namespace Pole.Features.WebServices.Application.Kubernetes;

public sealed record WebServiceKubernetesApplyFailed(string Message);
public sealed record WebServiceKubernetesApplied;
public sealed record WebServiceKubernetesApplyRejected(
    string FailureCode,
    string Message);

public union WebServiceKubernetesApplyOutcome(
    WebServiceKubernetesApplied,
    WebServiceKubernetesApplyFailed,
    WebServiceKubernetesApplyRejected);

public static class WebServiceKubernetesResourceManager
{
    public static async Task<bool> DeleteIngressForGenerationAsync(
        IKubernetes kubernetes,
        WebServiceInKubernetes webService,
        WebServiceDeploymentMetadata deployment,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task<bool>>? isCurrentGeneration = null)
    {
        var ingressName = webService.IngressName;
        V1Ingress current;
        try
        {
            current = await kubernetes.NetworkingV1.ReadNamespacedIngressAsync(
                ingressName,
                webService.Namespace,
                cancellationToken: cancellationToken);
        }
        catch (HttpOperationException exception) when (
            exception.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return true;
        }

        if (!IsOwnedIngress(current, webService) ||
            !TryGetDeploymentGeneration(current, out var currentGeneration) ||
            currentGeneration > GetDesiredGeneration(deployment))
        {
            return false;
        }
        if (isCurrentGeneration is not null &&
            !await isCurrentGeneration(cancellationToken))
        {
            return false;
        }

        try
        {
            await kubernetes.NetworkingV1.DeleteNamespacedIngressAsync(
                ingressName,
                webService.Namespace,
                body: new V1DeleteOptions
                {
                    Preconditions = new V1Preconditions
                    {
                        Uid = current.Metadata.Uid,
                        ResourceVersion = current.Metadata.ResourceVersion
                    }
                },
                cancellationToken: cancellationToken);
        }
        catch (HttpOperationException exception) when (
            exception.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return true;
        }
        catch (HttpOperationException exception) when (
            exception.Response?.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }

        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var remaining = await kubernetes.NetworkingV1.ReadNamespacedIngressAsync(
                    ingressName,
                    webService.Namespace,
                    cancellationToken: cancellationToken);
                if (!IsOwnedIngress(remaining, webService) ||
                    !TryGetDeploymentGeneration(remaining, out var remainingGeneration) ||
                    remainingGeneration > GetDesiredGeneration(deployment))
                {
                    return false;
                }
            }
            catch (HttpOperationException exception) when (
                exception.Response?.StatusCode == HttpStatusCode.NotFound)
            {
                return true;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        return false;
    }

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

        if (ingress is not null)
        {
            await KubernetesExtensions.CreateIfNotExistAsync(() =>
                kubernetes.NetworkingV1.CreateNamespacedIngressAsync(ingress, webService.Namespace, cancellationToken: cancellationToken));
        }

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

        if (ingress is null)
        {
            await KubernetesExtensions.DeleteIfExistsAsync(() =>
                kubernetes.NetworkingV1.DeleteNamespacedIngressAsync(
                    webService.IngressName,
                    webService.Namespace,
                    cancellationToken: cancellationToken));
        }
        else
        {
            await kubernetes.NetworkingV1.ReplaceNamespacedIngressAsync(
                ingress,
                ingress.Metadata.Name,
                webService.Namespace,
                cancellationToken: cancellationToken);
        }
    }

    public static async Task<WebServiceKubernetesApplyOutcome> ApplyAsync(
        IKubernetes kubernetes,
        WebServiceInKubernetes webService,
        WebServiceDeploymentMetadata deploymentMetadata,
        CancellationToken cancellationToken = default,
        bool ingressAlreadyRemoved = false,
        Func<CancellationToken, Task<bool>>? isCurrentGeneration = null)
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
            var ingress = webService.ToIngress(deploymentMetadata);

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
            if (ingress is null)
            {
                if (!ingressAlreadyRemoved &&
                    !await DeleteIngressForGenerationAsync(
                        kubernetes,
                        webService,
                        deploymentMetadata,
                        cancellationToken,
                        isCurrentGeneration))
                {
                    return new WebServiceKubernetesApplyRejected(
                        "IngressRevocationPending",
                        "The current ingress could not be confirmed revoked for this deployment generation.");
                }
            }
            else
            {
                var ingressRejection = await ApplyIngressAsync(
                    kubernetes,
                    webService,
                    ingress,
                    deploymentMetadata,
                    cancellationToken,
                    isCurrentGeneration);
                if (ingressRejection is not null)
                    return ingressRejection;
            }
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

    private static async Task<WebServiceKubernetesApplyRejected?> ApplyIngressAsync(
        IKubernetes kubernetes,
        WebServiceInKubernetes webService,
        V1Ingress desired,
        WebServiceDeploymentMetadata deploymentMetadata,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<bool>>? isCurrentGeneration)
    {
        if (isCurrentGeneration is not null &&
            !await isCurrentGeneration(cancellationToken))
        {
            return new WebServiceKubernetesApplyRejected(
                "DeploymentGenerationSuperseded",
                "A newer deployment generation superseded this ingress apply.");
        }
        try
        {
            var created = await kubernetes.NetworkingV1.CreateNamespacedIngressAsync(
                desired,
                webService.Namespace,
                cancellationToken: cancellationToken);
            return await RejectSupersededIngressWriteAsync(
                kubernetes,
                webService,
                desired.Metadata.Name,
                created,
                cancellationToken,
                isCurrentGeneration);
        }
        catch (HttpOperationException exception) when (
            exception.Response?.StatusCode == HttpStatusCode.Conflict)
        {
            // An existing same-name ingress must be read and fenced before replacement.
        }

        var existing = await kubernetes.NetworkingV1.ReadNamespacedIngressAsync(
            desired.Metadata.Name,
            webService.Namespace,
            cancellationToken: cancellationToken);
        if (!IsOwnedIngress(existing, webService) ||
            !TryGetDeploymentGeneration(existing, out var existingGeneration) ||
            existingGeneration > GetDesiredGeneration(deploymentMetadata))
        {
            return new WebServiceKubernetesApplyRejected(
                "IngressOwnershipConflict",
                "A newer or unowned ingress cannot be replaced by this deployment generation.");
        }
        if (isCurrentGeneration is not null &&
            !await isCurrentGeneration(cancellationToken))
        {
            return new WebServiceKubernetesApplyRejected(
                "DeploymentGenerationSuperseded",
                "A newer deployment generation superseded this ingress replacement.");
        }

        desired.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
        var replaced = await kubernetes.NetworkingV1.ReplaceNamespacedIngressAsync(
            desired,
            desired.Metadata.Name,
            webService.Namespace,
            cancellationToken: cancellationToken);
        return await RejectSupersededIngressWriteAsync(
            kubernetes,
            webService,
            desired.Metadata.Name,
            replaced,
            cancellationToken,
            isCurrentGeneration);
    }

    private static async Task<WebServiceKubernetesApplyRejected?> RejectSupersededIngressWriteAsync(
        IKubernetes kubernetes,
        WebServiceInKubernetes webService,
        string ingressName,
        V1Ingress written,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<bool>>? isCurrentGeneration)
    {
        if (isCurrentGeneration is null || await isCurrentGeneration(cancellationToken))
            return null;

        var uid = written.Metadata?.Uid;
        var resourceVersion = written.Metadata?.ResourceVersion;
        if (!string.IsNullOrWhiteSpace(uid) &&
            !string.IsNullOrWhiteSpace(resourceVersion))
        {
            try
            {
                await kubernetes.NetworkingV1.DeleteNamespacedIngressAsync(
                    ingressName,
                    webService.Namespace,
                    body: new V1DeleteOptions
                    {
                        Preconditions = new V1Preconditions
                        {
                            Uid = uid,
                            ResourceVersion = resourceVersion
                        }
                    },
                    cancellationToken: cancellationToken);
            }
            catch (HttpOperationException exception) when (
                exception.Response?.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
            {
                // A newer generation has already removed or changed this exact ingress.
            }
        }

        return new WebServiceKubernetesApplyRejected(
            "DeploymentGenerationSuperseded",
            "A newer deployment generation superseded this ingress write.");
    }

    private static bool IsOwnedIngress(
        V1Ingress ingress,
        WebServiceInKubernetes webService)
    {
        if (ingress.Metadata?.Labels is null)
            return false;
        var expected = webService.PlacementIdentity.ToDictionary();
        return expected.All(pair =>
            ingress.Metadata.Labels.TryGetValue(pair.Key, out var value) &&
            string.Equals(value, pair.Value, StringComparison.Ordinal));
    }

    private static bool TryGetDeploymentGeneration(
        V1Ingress ingress,
        out long generation)
    {
        generation = 0;
        return ingress.Metadata.Annotations is not null &&
               ingress.Metadata.Annotations.TryGetValue(
                   "pole.server/deploymentGeneration",
                   out var value) &&
               long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out generation);
    }

    private static long GetDesiredGeneration(WebServiceDeploymentMetadata metadata) =>
        metadata is WebServiceResourceDeploymentMetadata deployment
            ? deployment.DesiredGeneration
            : throw new InvalidOperationException(
                "Resource ingress reconciliation requires a resource deployment generation.");

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
