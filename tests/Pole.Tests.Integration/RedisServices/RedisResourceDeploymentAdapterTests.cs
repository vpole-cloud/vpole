using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using k8s;
using Pole.Features.RedisServices.Application.Kubernetes;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceDeployments.Application.Reconciliation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.Shared.Models;
using Xunit;
using Redis = Pole.Features.RedisServices.Domain.Redis;

namespace Pole.Tests.Integration.RedisServices;

public sealed class RedisResourceDeploymentAdapterTests
{
    [Fact]
    public async Task Apply_UsesContextClientNamespaceAndResolvedStorage()
    {
        var reader = new RecordingReader(CreateRedis());
        using var handler = new RecordingKubernetesHandler();
        using var kubernetes = CreateKubernetes(handler);
        var subject = new RedisResourceDeploymentAdapter(reader);

        var result = await subject.ApplyAsync(CreateContext(kubernetes));

        var succeeded =
            Assert.IsType<ResourceDeploymentAdapterResult.Succeeded>(result);
        var endpoint = Assert.IsType<RedisResourceDeploymentEndpoint>(
            Assert.Single(succeeded.Endpoints));
        Assert.Equal(
            $"redis-cache-svc.{KubernetesNamespace}.svc.cluster.local",
            endpoint.Host);
        Assert.Equal(6379, endpoint.Port);
        Assert.Equal("redis", endpoint.Scheme);
        Assert.Equal([(TenantId, ResourceId)], reader.Requests);
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
    }

    [Fact]
    public async Task Apply_WithoutResolvedStorage_FailsBeforeLookupOrKubernetes()
    {
        var reader = new RecordingReader(CreateRedis());
        using var handler = new RecordingKubernetesHandler();
        using var kubernetes = CreateKubernetes(handler);
        var subject = new RedisResourceDeploymentAdapter(reader);

        var result = await subject.ApplyAsync(
            CreateContext(kubernetes, includeStorage: false));

        var failed =
            Assert.IsType<ResourceDeploymentAdapterResult.Failed>(result);
        Assert.Equal("StorageConfigurationMissing", failed.FailureCode);
        Assert.False(failed.Retryable);
        Assert.Empty(reader.Requests);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Delete_UsesContextClientAndPersistedNamespace()
    {
        var reader = new RecordingReader(CreateRedis());
        using var handler = new RecordingKubernetesHandler();
        using var kubernetes = CreateKubernetes(handler);
        var subject = new RedisResourceDeploymentAdapter(reader);

        var result = await subject.DeleteAsync(CreateContext(kubernetes));

        var succeeded =
            Assert.IsType<ResourceDeploymentAdapterResult.Succeeded>(result);
        Assert.Empty(succeeded.Endpoints);
        Assert.Equal([(TenantId, ResourceId)], reader.Requests);
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
        IKubernetes kubernetes,
        bool includeStorage = true)
    {
        var placement = ResolvedResourcePlacement.Create(
            RuntimeCellId,
            TargetId,
            KubernetesNamespace,
            runtimeCellGeneration: 1,
            targetWorkloadConfigurationRevision: 3,
            ingress: null,
            includeStorage
                ? ResolvedStorageConfiguration.Create(
                    StorageProfileId,
                    storageProfileRevision: 7,
                    StorageClassName)
                : null,
            NowUtc);
        var deployment = ResourceDeploymentFactory.Create(
            TenantId,
            ResourceDeploymentResourceTypes.Redis,
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

    private static Redis CreateRedis() =>
        RedisFactory.Create(
            tenantId: TenantId,
            name: "redis-cache",
            resourceGroupId: ResourceGroupId,
            spec: new RedisSpec(
                new Storage(12, StorageProfileId),
                new Resources(
                    Requests: new Resource(1, 256),
                    Limits: new Resource(2, 512))),
            options: RedisFactory.DefaultCreateOptions with
            {
                Id = ResourceId,
                Tags = ImmutableHashSet.Create("cache")
            });

    private sealed class RecordingReader(Redis redis)
        : IRedisResourceDeploymentReader
    {
        public List<(string TenantId, string ResourceId)> Requests { get; } = [];

        public Task<Redis?> FindAsync(
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
