using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using k8s;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Kubernetes;
using Pole.Features.RedisServices.Application.Persistence;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Xunit;
using Redis = Pole.Features.RedisServices.Domain.Redis;

namespace Pole.Features.RedisServices.Tests;

public sealed class RedisResourceDeploymentAdapterTests
{
    [Fact]
    public async Task Apply_UsesContextClientNamespaceAndResolvedStorage()
    {
        var loader = new RecordingRedisLoader(CreateRedis());
        using var handler = new RecordingKubernetesHandler();
        using var kubernetes = CreateKubernetes(handler);
        var subject = new RedisResourceDeploymentAdapter(
            loader.InvokeAsync,
            new FixedKubernetesClientProvider(kubernetes));

        var result = await subject.ApplyAsync(CreateContext());

        var succeeded =
            Assert.IsType<ResourceDeploymentAdapterResult.Succeeded>(result);
        var endpoint = Assert.Single(succeeded.Endpoints);
        Assert.Equal("Redis", endpoint.Kind);
        Assert.Equal(
            $"redis-cache-svc.{KubernetesNamespace}.svc.cluster.local",
            endpoint.Host);
        Assert.Equal(6379, endpoint.Port);
        Assert.Equal("redis", endpoint.Scheme);
        Assert.Equal([(TenantId, ResourceId)], loader.Requests);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(
            handler.Requests,
            request => Assert.Contains(
                $"/namespaces/{KubernetesNamespace}/",
                request.Path,
                StringComparison.Ordinal));

        using var statefulSet = JsonDocument.Parse(
            Assert.Single(
                handler.Requests,
                request => request.Path.EndsWith(
                    "/statefulsets",
                    StringComparison.Ordinal)).Body);
        var root = statefulSet.RootElement;
        Assert.Equal(
            KubernetesNamespace,
            root.GetProperty("metadata").GetProperty("namespace").GetString());
        Assert.Equal(
            StorageClassName,
            root.GetProperty("spec")
                .GetProperty("volumeClaimTemplates")[0]
                .GetProperty("spec")
                .GetProperty("storageClassName")
                .GetString());
        var annotations = root
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
        var podSpec = root
            .GetProperty("spec")
            .GetProperty("template")
            .GetProperty("spec");
        var resources = podSpec.GetProperty("containers")[0]
            .GetProperty("resources");
        Assert.Equal(
            "250m",
            resources.GetProperty("requests").GetProperty("cpu").GetString());
        Assert.Equal(
            "1Gi",
            resources.GetProperty("limits").GetProperty("memory").GetString());
    }

    [Fact]
    public async Task Apply_WithoutResolvedStorage_FailsBeforeLookupOrKubernetes()
    {
        var loader = new RecordingRedisLoader(CreateRedis());
        using var handler = new RecordingKubernetesHandler();
        using var kubernetes = CreateKubernetes(handler);
        var subject = new RedisResourceDeploymentAdapter(
            loader.InvokeAsync,
            new FixedKubernetesClientProvider(kubernetes));

        var result = await subject.ApplyAsync(
            CreateContext(includeStorage: false));

        var failed =
            Assert.IsType<ResourceDeploymentAdapterResult.Failed>(result);
        Assert.Equal("StorageConfigurationMissing", failed.FailureCode);
        Assert.False(failed.Retryable);
        Assert.Empty(loader.Requests);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Delete_UsesContextClientAndPersistedNamespace()
    {
        var loader = new RecordingRedisLoader(CreateRedis());
        using var handler = new RecordingKubernetesHandler();
        using var kubernetes = CreateKubernetes(handler);
        var subject = new RedisResourceDeploymentAdapter(
            loader.InvokeAsync,
            new FixedKubernetesClientProvider(kubernetes));

        var result = await subject.DeleteAsync(CreateContext());

        var succeeded =
            Assert.IsType<ResourceDeploymentAdapterResult.Succeeded>(result);
        Assert.Empty(succeeded.Endpoints);
        Assert.Equal([(TenantId, ResourceId)], loader.Requests);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Contains(
                $"/namespaces/{KubernetesNamespace}/",
                request.Path,
                StringComparison.Ordinal);
        });
    }

    private static IKubernetes CreateKubernetes(DelegatingHandler handler) =>
        new Kubernetes(
            new KubernetesClientConfiguration
            {
                Host = "http://exact-target.test"
            },
            handler);

    private static ResourceDeploymentReconciliationContext CreateContext(
        bool includeStorage = true)
    {
        var placement = new ResourceDeploymentResolvedPlacementSnapshot(
            TargetId,
            TargetWorkloadConfigurationRevision: 1,
            KubernetesNamespace,
            Ingress: null,
            includeStorage
                ? new ResourceDeploymentStorageConfiguration(
                    StorageProfileId,
                    StorageProfileRevision: 7,
                    StorageClassName)
                : null,
            new ResourceDeploymentComputeConfiguration(
                "507f1f77bcf86cd799439019",
                ComputeProfileRevision: 1,
                TargetId,
                "General purpose",
                new ResourceDeploymentComputeResourceValues(250, 256),
                new ResourceDeploymentComputeResourceValues(1000, 1024),
                ComputeArchitecture.Any,
                [],
                [],
                [],
                [],
                [],
                RuntimeClassName: null));
        var deployment = new ResourceDeploymentReconciliationSnapshot(
            DeploymentId,
            TenantId,
            RedisResourceDeployment.ResourceType,
            ResourceId,
            ResourceGroupId,
            IsolationScopeId,
            RuntimeCellId,
            ResourceRevision: 1,
            FixedReplicas: 1,
            DesiredGeneration: 1,
            UpdatedAtUtc: NowUtc,
            Placement: placement);

        return new ResourceDeploymentReconciliationContext(deployment);
    }

    private static Redis CreateRedis() =>
        RedisFactory.Create(
            tenantId: TenantId,
            name: "redis-cache",
            resourceGroupId: ResourceGroupId,
            spec: new RedisSpec(
                new Storage(12, StorageProfileId)),
            options: RedisFactory.DefaultCreateOptions with
            {
                Id = ResourceId,
                Tags = ImmutableHashSet.Create("cache")
            });

    private sealed class RecordingRedisLoader(Redis redis)
    {
        public List<(string TenantId, string ResourceId)> Requests { get; } = [];

        public Task<Redis?> InvokeAsync(
            string tenantId,
            string redisId,
            CancellationToken cancellationToken = default)
        {
            Requests.Add((tenantId, redisId));
            return Task.FromResult<Redis?>(redis);
        }
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
                Content = new StringContent(
                    body,
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Path,
        string Body);

    private sealed class FixedKubernetesClientProvider(IKubernetes kubernetes)
        : IKubernetesClientProvider
    {
        public Task<IKubernetes> GetAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(kubernetes);
        public Task<IKubernetes> GetDefaultAsync(CancellationToken cancellationToken = default) => Task.FromResult(kubernetes);
    }

    private const string TenantId = "507f1f77bcf86cd799439011";
    private const string ResourceId = "507f1f77bcf86cd799439012";
    private const string ResourceGroupId = "507f1f77bcf86cd799439013";
    private const string IsolationScopeId = "507f1f77bcf86cd799439014";
    private const string RuntimeCellId = "507f1f77bcf86cd799439015";
    private const string TargetId = "507f1f77bcf86cd799439016";
    private const string DeploymentId = "507f1f77bcf86cd799439017";
    private const string StorageProfileId = "507f1f77bcf86cd799439018";
    private const string KubernetesNamespace = "exact-target-namespace";
    private const string StorageClassName = "target-fast-storage";
    private static readonly DateTime NowUtc =
        new(2026, 7, 29, 14, 0, 0, DateTimeKind.Utc);
}
