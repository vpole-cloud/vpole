using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using k8s;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.ResourceDeployments.Application.Reconciliation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Kubernetes;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.ResourceDeployments;
using Xunit;

namespace Pole.Tests.Integration.WebServices;

public sealed class WebServiceResourceDeploymentAdapterTests
{
    [Fact]
    public async Task Apply_UsesContextClientNamespaceAndResolvedIngress()
    {
        var webService = CreateWebService();
        var reader = new RecordingReader(webService);
        using var handler = new RecordingKubernetesHandler();
        using var kubernetes = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://kubernetes.test" },
            handler);
        var subject = new WebServiceResourceDeploymentAdapter(
            reader.LoadSnapshot,
            reader.LoadById,
            new UnexpectedRegistryAccessProvider());
        var context = CreateContext(kubernetes, includeIngress: true);

        var result = await subject.ApplyAsync(context);

        var succeeded = Assert.IsType<ResourceDeploymentAdapterResult.Succeeded>(result);
        var endpoint = Assert.IsType<IngressResourceDeploymentEndpoint>(
            Assert.Single(succeeded.Endpoints));
        Assert.Equal("api.example.test", endpoint.Host);
        Assert.Equal(443, endpoint.Port);
        Assert.Equal("https", endpoint.Scheme);
        Assert.Equal(
            [(TenantId, ResourceId, 1L)],
            reader.ApplyRequests);
        Assert.Equal(5, handler.Requests.Count);
        Assert.All(
            handler.Requests,
            request => Assert.Contains(
                $"/namespaces/{KubernetesNamespace}/",
                request.Path,
                StringComparison.Ordinal));

        using var ingress = JsonDocument.Parse(
            Assert.Single(
                handler.Requests,
                request => request.Path.EndsWith(
                    "/ingresses",
                    StringComparison.Ordinal)).Body);
        var root = ingress.RootElement;
        Assert.Equal(
            KubernetesNamespace,
            root.GetProperty("metadata").GetProperty("namespace").GetString());
        Assert.Equal(
            "nginx-private",
            root.GetProperty("spec").GetProperty("ingressClassName").GetString());
        Assert.Equal(
            "pole-private-issuer",
            root.GetProperty("metadata")
                .GetProperty("annotations")
                .GetProperty("cert-manager.io/cluster-issuer")
                .GetString());

        using var deployment = JsonDocument.Parse(
            Assert.Single(
                handler.Requests,
                request => request.Path.EndsWith(
                    "/deployments",
                    StringComparison.Ordinal)).Body);
        var annotations = deployment.RootElement
            .GetProperty("spec")
            .GetProperty("template")
            .GetProperty("metadata")
            .GetProperty("annotations");
        Assert.Equal(
            DeploymentId,
            annotations.GetProperty("pole.server/resourceDeploymentId").GetString());
        Assert.Equal(
            RuntimeCellId,
            annotations.GetProperty("pole.server/runtimeCellId").GetString());
        Assert.Equal(
            TargetId,
            annotations.GetProperty("pole.server/deploymentTargetId").GetString());
        Assert.Equal(
            "1",
            annotations.GetProperty("pole.server/deploymentGeneration").GetString());
        var resources = deployment.RootElement
            .GetProperty("spec")
            .GetProperty("template")
            .GetProperty("spec")
            .GetProperty("containers")[0]
            .GetProperty("resources");
        Assert.Equal(
            "250m",
            resources.GetProperty("requests").GetProperty("cpu").GetString());
        Assert.Equal(
            "1",
            resources.GetProperty("limits").GetProperty("cpu").GetString());
    }

    [Fact]
    public async Task Apply_WithoutResolvedIngress_FailsBeforeResourceLookupOrKubernetes()
    {
        var reader = new RecordingReader(CreateWebService());
        using var handler = new RecordingKubernetesHandler();
        using var kubernetes = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://kubernetes.test" },
            handler);
        var subject = new WebServiceResourceDeploymentAdapter(
            reader.LoadSnapshot,
            reader.LoadById,
            new UnexpectedRegistryAccessProvider());

        var result = await subject.ApplyAsync(
            CreateContext(kubernetes, includeIngress: false));

        var failed = Assert.IsType<ResourceDeploymentAdapterResult.Failed>(result);
        Assert.Equal("IngressConfigurationMissing", failed.FailureCode);
        Assert.False(failed.Retryable);
        Assert.Empty(reader.ApplyRequests);
        Assert.Empty(reader.DeleteRequests);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Delete_UsesOnlyTheContextClientAndPersistedNamespace()
    {
        var reader = new RecordingReader(CreateWebService());
        using var handler = new RecordingKubernetesHandler();
        using var kubernetes = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://kubernetes.test" },
            handler);
        var subject = new WebServiceResourceDeploymentAdapter(
            reader.LoadSnapshot,
            reader.LoadById,
            new UnexpectedRegistryAccessProvider());

        var result = await subject.DeleteAsync(
            CreateContext(kubernetes, includeIngress: true));

        var succeeded = Assert.IsType<ResourceDeploymentAdapterResult.Succeeded>(result);
        Assert.Empty(succeeded.Endpoints);
        Assert.Equal(6, handler.Requests.Count);
        Assert.Equal([(TenantId, ResourceId)], reader.DeleteRequests);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Contains(
                $"/namespaces/{KubernetesNamespace}/",
                request.Path,
                StringComparison.Ordinal);
        });
    }

    private static ResourceDeploymentReconciliationContext CreateContext(
        IKubernetes kubernetes,
        bool includeIngress)
    {
        var placement = ResolvedResourcePlacement.Create(
            RuntimeCellId,
            TargetId,
            KubernetesNamespace,
            runtimeCellGeneration: 1,
            targetWorkloadConfigurationRevision: 4,
            includeIngress
                ? ResolvedIngressConfiguration.Create(
                    "nginx-private",
                    "pole-private-issuer")
                : null,
            storage: null,
            compute: ResourceDeploymentTestData.Compute(),
            resolvedAtUtc: NowUtc);
        var deployment = ResourceDeploymentFactory.Create(
            TenantId,
            WebServiceResourceDeployment.ResourceType,
            ResourceId,
            ResourceGroupId,
            IsolationScopeId,
            placement,
            ResourcePlacementCapability.SingleTarget,
            runtimeCellReady: true,
            new ResourceDeploymentFactory.CreateOptions(
                DeploymentId,
                NowUtc));
        var runtimeCell = RuntimeCellFactory.Create(
            TenantId,
            ResourceGroupId,
            IsolationScopeId,
            TargetId,
            KubernetesNamespace,
            new RuntimeCellFactory.CreateOptions(
                RuntimeCellId,
                NowUtc));
        return new ResourceDeploymentReconciliationContext(
            deployment,
            runtimeCell,
            kubernetes);
    }

    private static WebService CreateWebService() =>
        WebServiceFactory.Create(
            name: "api",
            tenantId: TenantId,
            resourceGroupId: ResourceGroupId,
            spec: new WebServiceSpec(
                new ImageInfo("registry.example.test", "pole/api", "1.0"),
                Replicas: 2,
                Config: new WebServiceConfig(
                    ImmutableDictionary<string, string>.Empty,
                    [new PortMapping(80, "8080", "TCP", "http")]),
                Autoscaling: null,
                Probes: new ProbeSpec(
                    new ProbeConfig("/health", "8080"),
                    new ProbeConfig("/health", "8080"),
                    new ProbeConfig("/health", "8080"))),
            dnsRecord: new DnsRecordShortInfo(
                "507f1f77bcf86cd799439021",
                "api.example.test"),
            options: WebServiceFactory.DefaultOptions with
            {
                Id = ResourceId
            });

    private sealed class RecordingReader(WebService webService)
    {
        public List<(string TenantId, string ResourceId, long Generation)>
            ApplyRequests { get; } = [];
        public List<(string TenantId, string ResourceId)> DeleteRequests { get; } = [];

        public Task<WebServiceDeploymentPersistenceSnapshot?> LoadSnapshot(
            string tenantId,
            string webServiceId,
            long deploymentGeneration,
            CancellationToken cancellationToken = default)
        {
            ApplyRequests.Add((tenantId, webServiceId, deploymentGeneration));
            return Task.FromResult<WebServiceDeploymentPersistenceSnapshot?>(
                new WebServiceDeploymentPersistenceSnapshot(
                    webService,
                    ReleaseId,
                    webService.ActiveRevision,
                    NowUtc));
        }

        public Task<WebService?> LoadById(
            string tenantId,
            string webServiceId,
            CancellationToken cancellationToken = default)
        {
            DeleteRequests.Add((tenantId, webServiceId));
            return Task.FromResult<WebService?>(webService);
        }
    }

    private sealed class UnexpectedRegistryAccessProvider
        : IRuntimeRegistryAccessProvider
    {
        public Task<Result<RuntimeRegistryAccess>> ResolveAsync(
            string tenantId,
            CancellationToken cancellationToken = default) =>
            throw new Xunit.Sdk.XunitException(
                "An image without a pull secret must not request registry credentials.");
    }

    private sealed class RecordingKubernetesHandler : DelegatingHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? "{}"
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.AbsolutePath,
                body));
            return new HttpResponseMessage(
                request.Method == HttpMethod.Delete
                    ? HttpStatusCode.OK
                    : HttpStatusCode.Created)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Path,
        string Body);

    private const string TenantId = "507f1f77bcf86cd799439011";
    private const string ResourceId = "507f1f77bcf86cd799439012";
    private const string ResourceGroupId = "507f1f77bcf86cd799439013";
    private const string IsolationScopeId = "507f1f77bcf86cd799439014";
    private const string RuntimeCellId = "507f1f77bcf86cd799439015";
    private const string TargetId = "507f1f77bcf86cd799439016";
    private const string DeploymentId = "507f1f77bcf86cd799439017";
    private const string ReleaseId = "507f1f77bcf86cd799439018";
    private const string KubernetesNamespace = "exact-target-namespace";
    private static readonly DateTime NowUtc =
        new(2026, 7, 29, 14, 0, 0, DateTimeKind.Utc);
}
