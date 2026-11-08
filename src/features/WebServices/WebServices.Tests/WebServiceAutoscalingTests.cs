using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using k8s;
using Pole.Features.Core.Kubernetes;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Kubernetes;
using Pole.Features.WebServices.Domain;
using Pole.Workloads;
using Xunit;

namespace Pole.Features.WebServices.Tests;

[Trait("Layer", "Manifest")]
[Trait("Feature", "WebServices")]
public sealed class WebServiceAutoscalingTests
{
    [Theory]
    [InlineData("cpu", 80)]
    [InlineData("memory", 125)]
    public void Metrics_use_autoscaling_v2_resource_utilization(string resource, int utilization)
    {
        var service = Subject(new(2, 8, new(resource, utilization)));
        var hpa = service.ToHorizontalPodAutoscaler(Metadata());
        Assert.NotNull(hpa);
        Assert.Equal("autoscaling/v2", hpa.ApiVersion);
        Assert.Equal(service.PlacementIdentity.ToDictionary(), hpa.Metadata.Labels);
        Assert.Equal("1", hpa.Metadata.Annotations["pole.server/deploymentGeneration"]);
        var metric = Assert.Single(hpa.Spec.Metrics);
        Assert.Equal("Resource", metric.Type);
        Assert.Equal(resource, metric.Resource.Name);
        Assert.Equal("Utilization", metric.Resource.Target.Type);
        Assert.Equal(utilization, metric.Resource.Target.AverageUtilization);
    }

    [Fact]
    public async Task Reconciliation_preserves_scaled_replicas_and_switches_back_to_fixed()
    {
        var service = Subject(new(2, 8, new("cpu", 80)));
        using var handler = new ResourceHandler();
        using var client = Client(handler);
        AssertApplied(await Apply(client, service), handler);
        Assert.Equal(2, handler.Resource("deployments", service.DeploymentName)["spec"]!["replicas"]!.GetValue<int>());
        handler.Resource("deployments", service.DeploymentName)["spec"]!["replicas"] = 7;
        handler.Remove("horizontalpodautoscalers", service.AutoscalerName);
        AssertApplied(await Apply(client, service), handler);
        Assert.True(handler.Contains("horizontalpodautoscalers", service.AutoscalerName));
        handler.Resource("horizontalpodautoscalers", service.AutoscalerName)["spec"]!["minReplicas"] = 1;
        AssertApplied(await Apply(client, service), handler);
        Assert.Equal(2, handler.Resource("horizontalpodautoscalers", service.AutoscalerName)["spec"]!["minReplicas"]!
            .GetValue<int>());
        var changed = service with
        {
            WebService = service.WebService with
            {
                Spec = service.WebService.Spec with { Autoscaling = new(3, 9, new("memory", 120)) }
            }
        };

        AssertApplied(await Apply(client, changed), handler);
        Assert.Equal(7, handler.Resource("deployments", service.DeploymentName)["spec"]!["replicas"]!.GetValue<int>());
        Assert.Equal(3, handler.Resource("horizontalpodautoscalers", service.AutoscalerName)["spec"]!["minReplicas"]!
            .GetValue<int>());
        Assert.Contains(handler.Writes, item => item.Method == "PUT" && item.Path.Contains("horizontalpodautoscalers"));

        var fixedService = changed with
        {
            FixedReplicas = 4,
            WebService = changed.WebService with { Spec = changed.WebService.Spec with { Autoscaling = null } }
        };
        handler.Writes.Clear();
        AssertApplied(await Apply(client, fixedService), handler);
        Assert.False(handler.Contains("horizontalpodautoscalers", service.AutoscalerName));
        Assert.Equal(4, handler.Resource("deployments", service.DeploymentName)["spec"]!["replicas"]!.GetValue<int>());
        var deletion = Assert.Single(handler.Writes, item => item.Method == "DELETE");
        Assert.NotNull(deletion.Body!["preconditions"]!["uid"]);
        Assert.NotNull(deletion.Body["preconditions"]!["resourceVersion"]);
        Assert.Equal("DELETE", handler.Writes[0].Method);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Foreign_autoscaler_is_neither_replaced_nor_removed(bool fixedMode)
    {
        var service = Subject(new(2, 8, new("cpu", 80)));
        using var handler = new ResourceHandler();
        using var client = Client(handler);
        AssertApplied(await Apply(client, service), handler);
        handler.Resource("horizontalpodautoscalers", service.AutoscalerName)["metadata"]!["labels"]!
            ["pole.io/resource-id"] = "foreign";
        handler.Writes.Clear();
        if (fixedMode)
            service = service with
            {
                WebService = service.WebService with { Spec = service.WebService.Spec with { Autoscaling = null } }
            };

        Assert.IsType<WebServiceKubernetesApplyRejected>((await Apply(client, service)).Value);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WebServiceKubernetesResourceManager.DeleteAsync(client, Cleanup(service)));
        Assert.Empty(handler.Writes);
    }

    [Fact]
    public async Task Workload_cleanup_deletes_autoscaler_with_exact_identity()
    {
        var service = Subject(new(2, 8, new("cpu", 80)));
        using var handler = new ResourceHandler();
        using var client = Client(handler);
        AssertApplied(await Apply(client, service), handler);
        handler.Writes.Clear();

        await WebServiceKubernetesResourceManager.DeleteAsync(client, Cleanup(service));

        Assert.False(handler.Contains("horizontalpodautoscalers", service.AutoscalerName));
        Assert.Equal("DELETE", handler.Writes[0].Method);
        Assert.Contains("horizontalpodautoscalers", handler.Writes[0].Path);
        Assert.NotNull(handler.Writes[0].Body!["preconditions"]!["resourceVersion"]);
    }

    [Fact]
    public async Task Newer_generation_and_concurrent_autoscaler_change_block_fixed_replica_write()
    {
        var service = Subject(new(2, 8, new("cpu", 80)));
        using var handler = new ResourceHandler();
        using var client = Client(handler);
        AssertApplied(await Apply(client, service), handler);
        var fixedService = service with
        {
            FixedReplicas = 4,
            WebService = service.WebService with { Spec = service.WebService.Spec with { Autoscaling = null } }
        };
        handler.Resource("horizontalpodautoscalers", service.AutoscalerName)["metadata"]!["annotations"]!
            ["pole.server/deploymentGeneration"] = "2";
        handler.Writes.Clear();
        Assert.IsType<WebServiceKubernetesApplyRejected>((await Apply(client, fixedService)).Value);
        Assert.Empty(handler.Writes);

        handler.Resource("horizontalpodautoscalers", service.AutoscalerName)["metadata"]!["annotations"]!
            ["pole.server/deploymentGeneration"] = "1";
        handler.ConflictOnAutoscalerDelete = true;
        Assert.IsType<WebServiceKubernetesApplyFailed>((await Apply(client, fixedService)).Value);
        Assert.Equal(2, handler.Resource("deployments", service.DeploymentName)["spec"]!["replicas"]!.GetValue<int>());
    }

    [Fact]
    public async Task Autoscaler_is_not_created_for_a_foreign_same_name_deployment()
    {
        var service = Subject(new(2, 8, new("cpu", 80)));
        using var handler = new ResourceHandler();
        using var client = Client(handler);
        AssertApplied(await Apply(client, service), handler);
        handler.Remove("horizontalpodautoscalers", service.AutoscalerName);
        handler.Resource("deployments", service.DeploymentName)["metadata"]!["labels"]!
            ["pole.io/resource-id"] = "foreign";

        Assert.IsType<WebServiceKubernetesApplyFailed>((await Apply(client, service)).Value);
        Assert.False(handler.Contains("horizontalpodautoscalers", service.AutoscalerName));
    }

    [Fact]
    public async Task Prepared_service_has_no_native_side_effects()
    {
        var service = Subject(new(2, 8, new("cpu", 80)));
        service = service with
        {
            WebService = service.WebService with { Lifecycle = new PreparedWebServiceLifecycle() }
        };
        using var handler = new ResourceHandler();
        using var client = Client(handler);

        Assert.IsType<WebServiceKubernetesApplyRejected>((await Apply(client, service)).Value);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WebServiceKubernetesResourceManager.CreateAsync(client, service));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WebServiceKubernetesResourceManager.ReplaceAsync(client, service));
        Assert.Equal(0, handler.Requests);
    }

    [Theory]
    [InlineData("unknown", 80)]
    [InlineData("cpu", 0)]
    public async Task Invalid_metric_fails_before_native_changes(string type, int target)
    {
        using var handler = new ResourceHandler();
        using var client = Client(handler);
        var outcome = await Apply(client, Subject(new(2, 8, new(type, target))));
        Assert.IsType<WebServiceKubernetesApplyFailed>(outcome.Value);
        Assert.Equal(0, handler.Requests);
    }

    private static Task<WebServiceKubernetesApplyOutcome> Apply(IKubernetes client, WebServiceInKubernetes service) =>
        WebServiceKubernetesResourceManager.ApplyAsync(client, service, Metadata());

    private static void AssertApplied(WebServiceKubernetesApplyOutcome result, ResourceHandler handler) =>
        Assert.True(result is WebServiceKubernetesApplied,
            (result is WebServiceKubernetesApplyFailed failed ? failed.Message : result.Value.ToString()) +
            $"; mock failure: {handler.Failure}");

    private static WebServiceKubernetesCleanup Cleanup(WebServiceInKubernetes service) =>
        new(service.WebService.Name, service.Namespace, service.PlacementIdentity);

    private static WebServiceResourceDeploymentMetadata Metadata() =>
        WebServiceResourceDeploymentMetadata.Create(
            "release", "deployment", "cell", "target", 1, 1, DateTime.UnixEpoch);

    private static Kubernetes Client(DelegatingHandler handler) =>
        new(new KubernetesClientConfiguration { Host = "http://kubernetes.test" }, handler);

    private static WebServiceInKubernetes Subject(AutoscalingSpec scaling)
    {
        var service = WebServiceFactory.Create("autoscale", WebServiceTestData.TenantId,
            "507f1f77bcf86cd799439032", "507f1f77bcf86cd799439033",
            WebServiceTestData.CreateSpec().ToDomain() with { Autoscaling = scaling },
            WebServiceExposure.Create(WebServiceExposureMode.Private), WebServiceFactory.DefaultOptions);
        return service.ToKubernetes("workloads", null,
            new ResourceDeploymentComputeConfiguration("compute", 1, "target", "Compute", new(100, 128),
                new(500, 256), ComputeArchitecture.Any, [], [], [], [], [], null), 1,
            new WorkloadIdentityLabels(service.TenantId, service.ResourceGroupId, service.IsolationScopeId,
                "cell", "target",
                WebServiceResourceDeployment.KubernetesResourceTypeLabel, service.Id, "deployment"));
    }

    private sealed record Write(
        string Method,
        string Path,
        JsonObject? Body);

    private sealed class ResourceHandler : DelegatingHandler
    {
        private readonly Dictionary<string, JsonObject> resources = [];
        public List<Write> Writes { get; } = [];
        public int Requests { get; private set; }
        public bool ConflictOnAutoscalerDelete { get; set; }
        public Exception? Failure { get; private set; }

        public JsonObject Resource(string collection, string name) =>
            resources.Single(pair => pair.Key.EndsWith($"/{collection}/{name}", StringComparison.Ordinal)).Value;

        public void Remove(string collection, string name) =>
            resources.Remove(resources.Keys.Single(path =>
                path.EndsWith($"/{collection}/{name}", StringComparison.Ordinal)));

        public bool Contains(string collection, string name) =>
            resources.Keys.Any(path => path.EndsWith($"/{collection}/{name}", StringComparison.Ordinal));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            try
            {
                var response = await HandleAsync(request, ct);
                response.RequestMessage = request;
                return response;
            }
            catch (Exception error)
            {
                Failure = error;
                throw;
            }
        }

        private async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? null :
                JsonNode.Parse(await request.Content.ReadAsStringAsync(ct))!.AsObject();
            if (request.Method == HttpMethod.Get)
                return resources.TryGetValue(path, out var read)
                    ? Response(HttpStatusCode.OK, read) : Response(HttpStatusCode.NotFound);

            if (request.Method == HttpMethod.Delete)
            {
                if (!resources.TryGetValue(path, out var existing))
                    return Response(HttpStatusCode.NotFound);

                Writes.Add(new("DELETE", path, body));
                if (path.Contains("horizontalpodautoscalers", StringComparison.Ordinal) &&
                    (ConflictOnAutoscalerDelete ||
                     body?["preconditions"]?["uid"]?.ToString() != existing["metadata"]!["uid"]!.ToString() ||
                     body?["preconditions"]?["resourceVersion"]?.ToString() !=
                     existing["metadata"]!["resourceVersion"]!.ToString()))
                    return Response(HttpStatusCode.Conflict);

                resources.Remove(path);
                return Response(HttpStatusCode.OK);
            }

            var metadata = body!["metadata"]!.AsObject();
            var key = request.Method == HttpMethod.Post ? $"{path}/{metadata["name"]}" : path;
            if (request.Method == HttpMethod.Post && resources.ContainsKey(key))
                return Response(HttpStatusCode.Conflict);

            Writes.Add(new(request.Method.Method, key, body.DeepClone().AsObject()));
            metadata["uid"] = resources.TryGetValue(key, out var previous)
                ? previous["metadata"]!["uid"]!.ToString() : Guid.NewGuid().ToString();
            metadata["resourceVersion"] = Requests.ToString();
            resources[key] = body;
            return Response(request.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK, body);
        }

        private static HttpResponseMessage Response(HttpStatusCode status, JsonObject? body = null) => new(status)
        {
            Content = new StringContent(body?.ToJsonString() ?? "{}", Encoding.UTF8, "application/json")
        };
    }
}
