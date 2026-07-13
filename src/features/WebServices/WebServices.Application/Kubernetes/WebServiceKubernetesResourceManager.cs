using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Pole.Features.Core.Kubernetes;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Kubernetes;

public static class WebServiceKubernetesResourceManager
{
    public static async Task<Result> ApplyImagePullSecretAsync(
        IKubernetes kubernetes,
        WebServiceInKubernetes webService,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var secret = webService.ToImagePullSecret(username, password);
        if (secret is null)
            return Result.Success();

        return await CreateOrReplaceAsync(
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

    public static async Task<Result> ApplyAsync(
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
            var applyResult = await CreateOrReplaceAsync(
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
            if (applyResult is Failure)
                return applyResult;

            stage = "applying the Deployment";
            applyResult = await CreateOrReplaceAsync(
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
            if (applyResult is Failure)
                return applyResult;

            stage = "applying the Service";
            applyResult = await CreateOrReplaceAsync(
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
            if (applyResult is Failure)
                return applyResult;

            stage = "applying the Ingress";
            applyResult = await CreateOrReplaceAsync(
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
            if (applyResult is Failure)
                return applyResult;

            var serviceMonitorObjectResult = ToJsonObject(serviceMonitor);
            if (!serviceMonitorObjectResult.TryGetValue(out var serviceMonitorObject, out var serviceMonitorFailure))
                return serviceMonitorFailure;

            stage = "applying the ServiceMonitor";
            applyResult = await CreateOrReplaceAsync(
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
                    var desiredResult = ToJsonObject(serviceMonitor);
                    if (!desiredResult.TryGetValue(out var desired, out var desiredFailure))
                        return desiredFailure;

                    var copyResourceVersionResult = CopyResourceVersion(existing, desired);
                    if (copyResourceVersionResult is Failure)
                        return copyResourceVersionResult;

                    await kubernetes.CustomObjects.ReplaceNamespacedCustomObjectAsync(
                        desired,
                        group: "monitoring.coreos.com",
                        version: "v1",
                        namespaceParameter: webService.Namespace,
                        plural: "servicemonitors",
                        name: webService.ServiceMonitorName,
                        cancellationToken: cancellationToken);

                    return Result.Success();
                });
            if (applyResult is Failure)
                return applyResult;

            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failures.ExternalService(
                $"Unable to apply web service deployment in Kubernetes while {stage}.",
                "Kubernetes",
                ex.Message);
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

    private static async Task<Result> CreateOrReplaceAsync(Func<Task> createCall, Func<Task> replaceCall) =>
        await CreateOrReplaceAsync(
            createCall,
            async () =>
            {
                await replaceCall();
                return Result.Success();
            });

    private static async Task<Result> CreateOrReplaceAsync(Func<Task> createCall, Func<Task<Result>> replaceCall)
    {
        try
        {
            await createCall();
            return Result.Success();
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.Conflict)
        {
            return await replaceCall();
        }
    }

    private static void PreserveServiceOwnedFields(V1Service desired, V1Service existing)
    {
        desired.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
        desired.Spec.ClusterIP = existing.Spec.ClusterIP;
        desired.Spec.ClusterIPs = existing.Spec.ClusterIPs;
        desired.Spec.HealthCheckNodePort = existing.Spec.HealthCheckNodePort;
    }

    private static Result<JsonObject> ToJsonObject(object value)
    {
        var node = JsonSerializer.SerializeToNode(value);
        return node is JsonObject jsonObject
            ? Result.Success(jsonObject)
            : Failures.Internal("Kubernetes custom object serialization produced an empty object.");
    }

    private static Result CopyResourceVersion(object existing, JsonObject desired)
    {
        var existingResult = ToJsonObject(existing);
        if (!existingResult.TryGetValue(out var existingJson, out var existingFailure))
            return existingFailure;

        var existingMetadata = existingJson["metadata"]?.AsObject();
        var resourceVersion = existingMetadata?["resourceVersion"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(resourceVersion))
        {
            return Failures.Internal("Existing Kubernetes custom object does not include metadata.resourceVersion.");
        }

        var desiredMetadata = desired["metadata"]?.AsObject();
        if (desiredMetadata is null)
        {
            desiredMetadata = [];
            desired["metadata"] = desiredMetadata;
        }

        desiredMetadata["resourceVersion"] = resourceVersion;
        return Result.Success();
    }
}

