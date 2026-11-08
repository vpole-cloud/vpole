using Pole.Features.Core.Kubernetes;
using System.Net;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Collections.Immutable;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Application.Kubernetes;
using Pole.Tests.Kubernetes.Infrastructure;
using Pole.Testing;
using Xunit;

namespace Pole.Tests.Kubernetes.WebServices;

[Trait("Layer", "Kubernetes")]
[Trait("Concern", "Feature")]
[Trait("Scope", "SingleCluster")]
[Trait("Feature", "WebServices")]
public sealed class WebServiceKubernetesTests(KubernetesClusterFixture fixture)
    : KubernetesIntegrationTestBase(fixture)
{
    [KubernetesFact("monitoring")]
    public async Task ApplyAsync_CreatesWebServiceResourcesWithReleaseMetadata()
    {
        var webService = BuildSourceBuildE2eWebService();
        var result = await WebServiceKubernetesResourceManager.ApplyAsync(
            Kubernetes,
            ToKubernetes(webService),
            WebServiceResourceDeploymentMetadata.Create(
                "release-k8s",
                "deployment-44",
                "cell-44",
                "target-44",
                1,
                1,
                DateTime.UtcNow));

        AssertResultSucceeded(result);

        var deployment = await Kubernetes.AppsV1.ReadNamespacedDeploymentAsync(webService.Name, NamespaceName);
        Assert.Equal("release-k8s", deployment.Spec.Template.Metadata.Annotations["pole.server/releaseId"]);
    }

    private WebService BuildSourceBuildE2eWebService()
    {
        var spec = new WebServiceSpec(
            new ImageInfo("docker.io", "library/nginx", "1.27",
                SecretName: null),
            Config: new WebServiceConfig()
            {
                EnvironmentVariables = ImmutableDictionary<string, string>.Empty,
                PortMappings = [new PortMapping(80, "8080", "TCP", "http")],
                ConnectionEnvironmentReferences = ImmutableDictionary<string, ConnectionEnvironmentReference>.Empty,
                SecretEnvironmentReferences = ImmutableDictionary<string, SecretEnvironmentReference>.Empty
            },
            Autoscaling: null,
            Probes: new ProbeSpec(
                new ProbeConfig("/", "8080"),
                new ProbeConfig("/", "8080"),
                new ProbeConfig("/", "8080")));

        return WebServiceFactory.Create(
            name: "source-build-e2e",
            tenantId: TestPlatformDefaults.TenantId,
            resourceGroupId: ResourceGroupId,
            isolationScopeId: IsolationScopeId,
            spec: spec,
            exposure: WebServiceExposure.Create(WebServiceExposureMode.Public),
            options: WebServiceFactory.DefaultOptions with { Id = SourceBuildWebServiceId });
    }

    private static void AssertResultSucceeded(WebServiceKubernetesApplyOutcome result)
    {
        if (result is WebServiceKubernetesApplyFailed failure)
            throw new Xunit.Sdk.XunitException(
                $"Kubernetes deployment apply failed: {failure.Message}");
    }

    [KubernetesFact("monitoring")]
    public async Task Lifecycle_creates_a_reachable_web_service_and_deletes_its_resources()
    {
        var webService = BuildTestWebService();

        await WebServiceKubernetesResourceManager.CreateAsync(
            Kubernetes,
            ToKubernetes(webService));

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
        using var httpResponse = await WaitForIngressHttpResponseAsync(ingress);

        Assert.Equal("Development", configMap.Data["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal(1, deployment.Spec.Replicas);
        Assert.Equal("docker.io/nginx:1.27", deployment.Spec.Template.Spec.Containers.Single().Image);
        Assert.Equal(80, service.Spec.Ports.Single().Port);
        Assert.Equal(BuildIngressHost(), ingress.Spec.Rules.Single().Host);
        Assert.NotNull(serviceMonitor);
        Assert.Equal(HttpStatusCode.OK, httpResponse.StatusCode);
        Assert.Contains("nginx", await httpResponse.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        await WebServiceKubernetesResourceManager.DeleteAsync(Kubernetes,
            new WebServiceKubernetesCleanup(webService.Name, NamespaceName,
                ToKubernetes(webService).PlacementIdentity));

        await Task.WhenAll(
            AssertResourceDeletedAsync(() => Kubernetes.CoreV1.ReadNamespacedConfigMapAsync(
                WebServiceInKubernetes.BuildConfigMapName(webService.Name),
                NamespaceName)),
            AssertResourceDeletedAsync(() => Kubernetes.AppsV1.ReadNamespacedDeploymentAsync(webService.Name, NamespaceName)),
            AssertResourceDeletedAsync(() => Kubernetes.CoreV1.ReadNamespacedServiceAsync(
                WebServiceInKubernetes.BuildServiceName(webService.Name),
                NamespaceName)),
            AssertResourceDeletedAsync(() => Kubernetes.NetworkingV1.ReadNamespacedIngressAsync(
                WebServiceInKubernetes.BuildIngressName(webService.Name),
                NamespaceName)),
            AssertResourceDeletedAsync(() => Kubernetes.CustomObjects.GetNamespacedCustomObjectAsync(
                group: "monitoring.coreos.com",
                version: "v1",
                namespaceParameter: NamespaceName,
                plural: "servicemonitors",
                name: $"{webService.Name}-sm")));
    }

    private WebService BuildTestWebService()
    {
        var spec = WebServiceKubernetesTestData.Spec().ToDomain() with
        {
            Image = new ImageInfo("docker.io", "nginx", "1.27",
                SecretName: null),
            Config = new WebServiceConfig()
            {
                EnvironmentVariables = new Dictionary<string, string>
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Development"
                }.ToImmutableDictionary(),
                PortMappings = [new PortMapping(80, "80", "TCP", "http")],
                ConnectionEnvironmentReferences = ImmutableDictionary<string, ConnectionEnvironmentReference>.Empty,
                SecretEnvironmentReferences = ImmutableDictionary<string, SecretEnvironmentReference>.Empty
            },
            Probes = new ProbeSpec(
                Startup: new ProbeConfig("/", "80"),
                Liveness: new ProbeConfig("/", "80"),
                Readiness: new ProbeConfig("/", "80"))
        };

        var options = WebServiceFactory.DefaultOptions with
        {
            Id = WebServiceId
        };

        return WebServiceFactory.Create(
            name: "web-integration",
            tenantId: TestPlatformDefaults.TenantId,
            resourceGroupId: ResourceGroupId,
            isolationScopeId: IsolationScopeId,
            spec: spec,
            exposure: WebServiceExposure.Create(WebServiceExposureMode.Public),
            options: options);
    }

    private string BuildIngressHost() => $"web-{NamespaceName}.{Target.PublicBaseDomain}";

    private WebServiceInKubernetes ToKubernetes(WebService webService) =>
        webService.ToKubernetes(
            NamespaceName,
            ResourceDeploymentIngressConfiguration.Create(
                "traefik",
                "pole-lab",
                Target.PublicBaseDomain,
                [BuildIngressHost()]),
            KubernetesComputeTestData.ContractCompute(
                "507f1f77bcf86cd799439020"),
            fixedReplicas: 1,
            new WorkloadIdentityLabels(
                webService.TenantId,
                webService.ResourceGroupId,
                IsolationScopeId,
                "cell-44",
                "target-44",
                "WebService",
                webService.Id,
                "deployment-44"));

    private const string SourceBuildWebServiceId = "507f1f77bcf86cd799439021";
    private const string WebServiceId = "507f1f77bcf86cd799439022";
    private const string ResourceGroupId = "507f1f77bcf86cd799439023";
    private const string IsolationScopeId = "507f1f77bcf86cd799439024";

    private async Task WaitForDeploymentAvailableAsync(string deploymentName, int timeoutSeconds = 60)
    {
        // Use monotonic elapsed time instead of a wall-clock deadline.
        var elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var deployment = await Kubernetes.AppsV1.ReadNamespacedDeploymentStatusAsync(deploymentName, NamespaceName);
            if (deployment.Status.AvailableReplicas >= 1)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw await CreateTimeoutExceptionAsync(
            $"Deployment '{deploymentName}' did not become available in namespace '{NamespaceName}'.");
    }

    private async Task<HttpResponseMessage> WaitForIngressHttpResponseAsync(V1Ingress ingress, int timeoutSeconds = 90)
    {
        var host = ingress.Spec.Rules.Single().Host;
        var baseUri = Target.Ingress.HttpBaseUri;
        // Use monotonic elapsed time instead of a wall-clock deadline.
        var elapsed = Stopwatch.StartNew();
        Exception? lastError = null;

        using var httpClient = new HttpClient(CreateIngressHttpHandler())
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(10)
        };

        while (elapsed.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
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

        throw await CreateTimeoutExceptionAsync(
            $"Ingress for host '{host}' did not return a successful response via '{baseUri}' in namespace '{NamespaceName}'.",
            lastError);
    }

    private static HttpMessageHandler CreateIngressHttpHandler() =>
        new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

    private static async Task AssertResourceDeletedAsync<T>(Func<Task<T>> readCall, int timeoutSeconds = 30)
    {
        // Use monotonic elapsed time instead of a wall-clock deadline.
        var elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
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
