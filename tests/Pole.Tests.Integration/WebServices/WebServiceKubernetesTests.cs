using System.Net;
using System.Net.Http.Headers;
using System.Collections.Immutable;
using System.Text.Json;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Application.Kubernetes;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.WebServices;

public sealed class WebServiceKubernetesTests(KubernetesClusterFixture fixture)
    : KubernetesIntegrationTestBase(fixture)
{
    [KubernetesFact]
    public async Task ApplyAsync_CreatesWebServiceResourcesWithReleaseMetadata()
    {
        var webService = BuildSourceBuildE2eWebService();
        var result = await WebServiceKubernetesResourceManager.ApplyAsync(
            Kubernetes,
            webService.ToKubernetes(),
            new WebServiceDeploymentMetadata("release-k8s", 1, DateTime.UtcNow));

        if (result is Failure failure)
        {
            var details = failure is ExternalServiceFailure externalFailure
                ? externalFailure.Details
                : failure.Message;
            throw new Xunit.Sdk.XunitException($"Kubernetes ApplyAsync failed: {details}");
        }

        var deployment = await Kubernetes.AppsV1.ReadNamespacedDeploymentAsync(webService.Name, NamespaceName);
        Assert.Equal("release-k8s", deployment.Spec.Template.Metadata.Annotations["pole.server/releaseId"]);
    }

    private WebService BuildSourceBuildE2eWebService()
    {
        var spec = new WebServiceSpec(
            NamespaceName,
            new ImageInfo("docker.io", "library/nginx", "1.27"),
            Replicas: 1,
            Resources: new Resources(new Resource(1, 64), new Resource(1, 128)),
            Config: new WebServiceConfig(
                ImmutableDictionary<string, string>.Empty,
                [new PortMapping(80, "8080", "TCP", "http")]),
            Autoscaling: null,
            Probes: new ProbeSpec(
                new ProbeConfig("/", "8080"),
                new ProbeConfig("/", "8080"),
                new ProbeConfig("/", "8080")));

        return WebServiceFactory.Create(
            name: "source-build-e2e",
            tenantId: TestData.TenantId,
            resourceGroupId: "rg-k8s",
            spec: spec,
            dnsRecord: new DnsRecordShortInfo("dns-k8s", BuildIngressHost()),
            options: WebServiceFactory.DefaultOptions with { Id = "source-build-e2e" });
    }

    [KubernetesFact]
    public async Task CreateAsync_CreatesWebServiceResourcesInCluster()
    {
        var webService = BuildTestWebService();

        await WebServiceKubernetesResourceManager.CreateAsync(Kubernetes, webService.ToKubernetes());

        var configMap = await Kubernetes.CoreV1.ReadNamespacedConfigMapAsync(
            WebServiceInKubernetes.BuildConfigMapName(webService.Name),
            NamespaceName);
        var deployment = await Kubernetes.AppsV1.ReadNamespacedDeploymentAsync(webService.Name, NamespaceName);
        var service = await Kubernetes.CoreV1.ReadNamespacedServiceAsync(
            WebServiceInKubernetes.BuildServiceName(webService.Name),
            NamespaceName);
        var ingress = await Kubernetes.NetworkingV1.ReadNamespacedIngressAsync(
            WebServiceInKubernetes.BuildIngressName(webService.Name),
            NamespaceName);
        var serviceMonitor = await Kubernetes.CustomObjects.GetNamespacedCustomObjectAsync(
            group: "monitoring.coreos.com",
            version: "v1",
            namespaceParameter: NamespaceName,
            plural: "servicemonitors",
            name: $"{webService.Name}-sm");

        await WaitForDeploymentAvailableAsync(webService.Name);
        var httpResponse = await WaitForIngressHttpResponseAsync(ingress);

        Assert.Equal("Development", configMap.Data["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal(1, deployment.Spec.Replicas);
        Assert.Equal("docker.io/nginx:1.27", deployment.Spec.Template.Spec.Containers.Single().Image);
        Assert.Equal(80, service.Spec.Ports.Single().Port);
        Assert.Equal(BuildIngressHost(), ingress.Spec.Rules.Single().Host);
        Assert.NotNull(serviceMonitor);
        Assert.Equal(HttpStatusCode.OK, httpResponse.StatusCode);
        Assert.Contains("nginx", await httpResponse.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [KubernetesFact]
    public async Task DeleteAsync_RemovesWebServiceResourcesFromCluster()
    {
        var webService = BuildTestWebService();

        await WebServiceKubernetesResourceManager.CreateAsync(Kubernetes, webService.ToKubernetes());
        await WebServiceKubernetesResourceManager.DeleteAsync(Kubernetes, webService.Name, NamespaceName);

        await AssertResourceDeletedAsync(() => Kubernetes.CoreV1.ReadNamespacedConfigMapAsync(
            WebServiceInKubernetes.BuildConfigMapName(webService.Name),
            NamespaceName));
        await AssertResourceDeletedAsync(() => Kubernetes.AppsV1.ReadNamespacedDeploymentAsync(webService.Name, NamespaceName));
        await AssertResourceDeletedAsync(() => Kubernetes.CoreV1.ReadNamespacedServiceAsync(
            WebServiceInKubernetes.BuildServiceName(webService.Name),
            NamespaceName));
        await AssertResourceDeletedAsync(() => Kubernetes.NetworkingV1.ReadNamespacedIngressAsync(
            WebServiceInKubernetes.BuildIngressName(webService.Name),
            NamespaceName));
        await AssertResourceDeletedAsync(() => Kubernetes.CustomObjects.GetNamespacedCustomObjectAsync(
            group: "monitoring.coreos.com",
            version: "v1",
            namespaceParameter: NamespaceName,
            plural: "servicemonitors",
            name: $"{webService.Name}-sm"));
    }

    private WebService BuildTestWebService()
    {
        var spec = TestData.ValidWebServiceSpec().ToDomain(NamespaceName) with
        {
            Image = new ImageInfo("docker.io", "nginx", "1.27"),
            Replicas = 1,
            Config = new WebServiceConfig(
                EnvironmentVariables: new Dictionary<string, string>
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Development"
                }.ToImmutableDictionary(),
                PortMappings:
                [
                    new PortMapping(80, "80", "TCP", "http")
                ]),
            Probes = new ProbeSpec(
                Startup: new ProbeConfig("/", "80"),
                Liveness: new ProbeConfig("/", "80"),
                Readiness: new ProbeConfig("/", "80"))
        };

        var options = WebServiceFactory.DefaultOptions with
        {
            Id = "web-service-k8s"
        };

        return WebServiceFactory.Create(
            name: "web-integration",
            tenantId: TestData.TenantId,
            resourceGroupId: "rg-k8s",
            spec: spec,
            dnsRecord: new DnsRecordShortInfo("dns-k8s", BuildIngressHost()),
            options: options);
    }

    private string BuildIngressHost() => $"web-{NamespaceName}.example.test";

    private async Task WaitForDeploymentAvailableAsync(string deploymentName, int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            var deployment = await Kubernetes.AppsV1.ReadNamespacedDeploymentStatusAsync(deploymentName, NamespaceName);
            if (deployment.Status.AvailableReplicas >= 1)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new TimeoutException($"Deployment '{deploymentName}' did not become available in namespace '{NamespaceName}'.");
    }

    private async Task<HttpResponseMessage> WaitForIngressHttpResponseAsync(V1Ingress ingress, int timeoutSeconds = 90)
    {
        var host = ingress.Spec.Rules.Single().Host;
        var baseUri = await ResolveIngressBaseUriAsync(ingress);
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        Exception? lastError = null;

        using var httpClient = new HttpClient(CreateIngressHttpHandler())
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(10)
        };

        while (DateTime.UtcNow < deadline)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.Host = host;
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

            try
            {
                var response = await httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                lastError = new HttpRequestException($"Ingress returned status code {(int)response.StatusCode}.");
                response.Dispose();
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new TimeoutException(
            $"Ingress for host '{host}' did not return a successful response via '{baseUri}' in namespace '{NamespaceName}'.",
            lastError);
    }

    private async Task<Uri> ResolveIngressBaseUriAsync(V1Ingress ingress)
    {
        var configuredBaseUri = Environment.GetEnvironmentVariable("POLE_K8S_INGRESS_BASE_URI");
        if (!string.IsNullOrWhiteSpace(configuredBaseUri))
        {
            return new Uri(configuredBaseUri, UriKind.Absolute);
        }

        configuredBaseUri = TryReadIngressBaseUriFromLocalConfig();
        if (!string.IsNullOrWhiteSpace(configuredBaseUri))
        {
            return new Uri(configuredBaseUri, UriKind.Absolute);
        }

        var loadBalancerIngress = ingress.Status?.LoadBalancer?.Ingress?.FirstOrDefault();
        var hostOrIp = loadBalancerIngress?.Ip ?? loadBalancerIngress?.Hostname;
        if (!string.IsNullOrWhiteSpace(hostOrIp))
        {
            return new Uri($"http://{hostOrIp}");
        }

        var ingressControllerService = await FindTraefikServiceAsync(ingress.Spec.IngressClassName);
        var serviceAddress = TryGetLoadBalancerAddress(ingressControllerService)
                             ?? await TryGetNodePortAddressAsync(ingressControllerService);
        if (serviceAddress is not null)
        {
            return serviceAddress;
        }

        throw new InvalidOperationException(
            "Ingress base URI is not configured. Set POLE_K8S_INGRESS_BASE_URI, add tests/Pole.Tests.Integration/testsettings.local.json, or expose a reachable Traefik load balancer or node port.");
    }

    private static string? TryReadIngressBaseUriFromLocalConfig()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "testsettings.local.json");
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            if (document.RootElement.TryGetProperty("POLE_K8S_INGRESS_BASE_URI", out var directValue) &&
                directValue.ValueKind == JsonValueKind.String)
            {
                return directValue.GetString();
            }

            if (document.RootElement.TryGetProperty("Kubernetes", out var kubernetesSection) &&
                kubernetesSection.ValueKind == JsonValueKind.Object &&
                kubernetesSection.TryGetProperty("IngressBaseUri", out var nestedValue) &&
                nestedValue.ValueKind == JsonValueKind.String)
            {
                return nestedValue.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static HttpMessageHandler CreateIngressHttpHandler() =>
        new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

    private async Task<V1Service> FindTraefikServiceAsync(string? ingressClassName)
    {
        var service = await TryReadServiceAsync("pole-traefik", ingressClassName ?? "traefik");
        if (service is not null)
        {
            return service;
        }

        service = await TryReadServiceAsync("pole-traefik", "traefik");
        if (service is not null)
        {
            return service;
        }

        var services = await Kubernetes.CoreV1.ListNamespacedServiceAsync("pole-traefik");
        service = services.Items.FirstOrDefault(s =>
            string.Equals(s.Metadata.Name, ingressClassName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.Metadata.Name, "traefik", StringComparison.OrdinalIgnoreCase) ||
            (s.Metadata.Labels?.TryGetValue("app.kubernetes.io/name", out var appName) == true &&
             string.Equals(appName, "traefik", StringComparison.OrdinalIgnoreCase)));

        return service ?? throw new InvalidOperationException(
            $"Could not resolve Traefik service for ingress class '{ingressClassName ?? "traefik"}'.");
    }

    private async Task<V1Service?> TryReadServiceAsync(string @namespace, string name)
    {
        try
        {
            return await Kubernetes.CoreV1.ReadNamespacedServiceAsync(name, @namespace);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static Uri? TryGetLoadBalancerAddress(V1Service service)
    {
        var loadBalancerIngress = service.Status?.LoadBalancer?.Ingress?.FirstOrDefault();
        var hostOrIp = loadBalancerIngress?.Ip ?? loadBalancerIngress?.Hostname;
        if (string.IsNullOrWhiteSpace(hostOrIp))
        {
            hostOrIp = service.Spec?.ExternalIPs?.FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(hostOrIp))
        {
            return null;
        }

        var port = service.Spec?.Ports?.FirstOrDefault(p => p.Port is 80 or 443)?.Port
                   ?? service.Spec?.Ports?.FirstOrDefault()?.Port
                   ?? 80;
        var scheme = port == 443 ? "https" : "http";
        return new Uri($"{scheme}://{hostOrIp}:{port}");
    }

    private async Task<Uri?> TryGetNodePortAddressAsync(V1Service service)
    {
        var nodePort = service.Spec?.Ports?.FirstOrDefault(p => p.Port == 80)?.NodePort
                       ?? service.Spec?.Ports?.FirstOrDefault()?.NodePort;
        if (nodePort is null)
        {
            return null;
        }

        var nodes = (await Kubernetes.CoreV1.ListNodeAsync()).Items;
        var nodeAddress = nodes
            .SelectMany(n => n.Status?.Addresses ?? [])
            .FirstOrDefault(a => a.Type is "ExternalIP" or "InternalIP")
            ?.Address;

        return string.IsNullOrWhiteSpace(nodeAddress)
            ? null
            : new Uri($"http://{nodeAddress}:{nodePort.Value}");
    }

    private static async Task AssertResourceDeletedAsync<T>(Func<Task<T>> readCall, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await readCall();
            }
            catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new TimeoutException("Resource was not deleted before the timeout elapsed.");
    }
}

