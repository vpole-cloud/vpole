using k8s;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Reconciliation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

[Collection(IntegrationTestCollection.Name)]
public sealed class ResourceDeploymentTargetRoutingTests(
    MongoReplicaSetFixture fixture)
    : IAsyncLifetime
{
    private const string TenantId = "507f1f77bcf86cd799439061";
    private const string ResourceId = "507f1f77bcf86cd799439062";
    private const string ResourceGroupId = "507f1f77bcf86cd799439063";
    private const string ScopeId = "507f1f77bcf86cd799439064";
    private const string RuntimeCellId = "507f1f77bcf86cd799439065";
    private static readonly ResourceTypeId TestResourceType =
        ResourceTypeId.Create("pole/test-resource");
    private const string TargetAId = "507f1f77bcf86cd799439066";
    private const string TargetBId = "507f1f77bcf86cd799439067";
    private const string DeploymentId = "507f1f77bcf86cd799439068";

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ReconciliationOnTargetB_UsesOnlyTargetBClient_AndNeverDefault()
    {
        var now = DateTime.UtcNow;
        var runtimeCell = RuntimeCellFactory.Create(
                TenantId,
                ResourceGroupId,
                ScopeId,
                TargetBId,
                "pole-routing-test",
                new RuntimeCellFactory.CreateOptions(RuntimeCellId, now))
            .MarkReady(expectedGeneration: 1, now.AddSeconds(1));
        var placement = ResolvedResourcePlacement.Create(
            RuntimeCellId,
            TargetBId,
            runtimeCell.KubernetesNamespace,
            runtimeCellGeneration: 1,
            targetWorkloadConfigurationRevision: 1,
            ingress: null,
            ResolvedStorageConfiguration.Create(
                "507f1f77bcf86cd799439069",
                storageProfileRevision: 1,
                "fast"),
            now);
        var deployment = ResourceDeploymentFactory.Create(
            TenantId,
            TestResourceType,
            ResourceId,
            ResourceGroupId,
            ScopeId,
            placement,
            ResourcePlacementCapability.SingleTarget,
            runtimeCellReady: true,
            new ResourceDeploymentFactory.CreateOptions(DeploymentId, now));
        await fixture.DbContext.RuntimeCells.InsertOneAsync(runtimeCell);
        await fixture.DbContext.ResourceDeployments.InsertOneAsync(deployment);

        using var targetA = CreateKubernetes("target-a.test");
        using var targetB = CreateKubernetes("target-b.test");
        var provider = new RecordingTwoTargetProvider(
            new Dictionary<string, IKubernetes>
            {
                [TargetAId] = targetA,
                [TargetBId] = targetB
            });
        var adapter = new RecordingAdapter();
        using var services = new ServiceCollection()
            .AddSingleton(adapter)
            .BuildServiceProvider();
        var settings = new ResourceDeploymentSettings
        {
            ClaimLeaseSeconds = 30,
            InitialRetryDelaySeconds = 1,
            MaximumRetryDelaySeconds = 10
        };
        using var metrics = new ResourceDeploymentMetrics();
        var reconciler = new ResourceDeploymentReconciler(
            fixture.DbContext,
            provider,
            new ResourceDeploymentAdapterCatalog(
            [
                ResourceDeploymentAdapterDescriptor.Create<RecordingAdapter>(
                    TestResourceType,
                    ResourcePlacementCapability.SingleTarget)
            ]),
            services,
            settings,
            new ResourceDeploymentRetryDelayPolicy(settings),
            metrics,
            TimeProvider.System,
            NullLogger<ResourceDeploymentReconciler>.Instance);

        await reconciler.HandleAsync(
            TenantId,
            DeploymentId,
            RuntimeCellId,
            generation: 1,
            ResourceDeploymentOperation.Apply);

        Assert.Equal([TargetBId], provider.RequestedTargetIds);
        Assert.Equal(0, provider.DefaultRequestCount);
        Assert.Same(targetB, adapter.Kubernetes);
        Assert.Equal(TargetBId, adapter.DeploymentTargetId);
        var saved = await fixture.DbContext.ResourceDeployments
            .Find(item => item.Id == DeploymentId)
            .SingleAsync();
        Assert.IsType<AppliedResourceDeploymentState>(saved.State);
    }

    [Fact]
    public async Task KubernetesTransportFailure_IsPersistedAsRetryableFailure()
    {
        var now = DateTime.UtcNow.AddMinutes(-1);
        var runtimeCell = RuntimeCellFactory.Create(
                TenantId,
                ResourceGroupId,
                ScopeId,
                TargetBId,
                "pole-routing-failure",
                new RuntimeCellFactory.CreateOptions(RuntimeCellId, now))
            .MarkReady(expectedGeneration: 1, now.AddSeconds(1));
        var deployment = ResourceDeploymentFactory.Create(
            TenantId,
            TestResourceType,
            ResourceId,
            ResourceGroupId,
            ScopeId,
            ResolvedResourcePlacement.Create(
                RuntimeCellId,
                TargetBId,
                runtimeCell.KubernetesNamespace,
                runtimeCellGeneration: 1,
                targetWorkloadConfigurationRevision: 1,
                ingress: null,
                ResolvedStorageConfiguration.Create(
                    "507f1f77bcf86cd799439069",
                    storageProfileRevision: 1,
                    "fast"),
                now.AddSeconds(1)),
            ResourcePlacementCapability.SingleTarget,
            runtimeCellReady: true,
            new ResourceDeploymentFactory.CreateOptions(
                DeploymentId,
                now.AddSeconds(1)));
        await fixture.DbContext.RuntimeCells.InsertOneAsync(runtimeCell);
        await fixture.DbContext.ResourceDeployments.InsertOneAsync(deployment);

        using var kubernetes = CreateKubernetes("target-b.test");
        var provider = new RecordingTwoTargetProvider(
            new Dictionary<string, IKubernetes>
            {
                [TargetBId] = kubernetes
            });
        using var services = new ServiceCollection()
            .AddSingleton<ThrowingTransportAdapter>()
            .BuildServiceProvider();
        var settings = new ResourceDeploymentSettings
        {
            ClaimLeaseSeconds = 30,
            InitialRetryDelaySeconds = 1,
            MaximumRetryDelaySeconds = 10
        };
        using var metrics = new ResourceDeploymentMetrics();
        var reconciler = new ResourceDeploymentReconciler(
            fixture.DbContext,
            provider,
            new ResourceDeploymentAdapterCatalog(
            [
                ResourceDeploymentAdapterDescriptor.Create<ThrowingTransportAdapter>(
                    TestResourceType,
                    ResourcePlacementCapability.SingleTarget)
            ]),
            services,
            settings,
            new ResourceDeploymentRetryDelayPolicy(settings),
            metrics,
            TimeProvider.System,
            NullLogger<ResourceDeploymentReconciler>.Instance);

        await reconciler.HandleAsync(
            TenantId,
            DeploymentId,
            RuntimeCellId,
            generation: 1,
            ResourceDeploymentOperation.Apply);

        var saved = await fixture.DbContext.ResourceDeployments
            .Find(item => item.Id == DeploymentId)
            .SingleAsync();
        var failed = Assert.IsType<FailedResourceDeploymentState>(saved.State);
        Assert.True(failed.Retryable);
        Assert.Equal("KubernetesTransportFailed", failed.FailureCode);
        Assert.Equal([TargetBId], provider.RequestedTargetIds);
        Assert.Equal(
            1L,
            await fixture.DbContext.CountOutboxMessagesAsync(
                "resource-deployment-failed"));
    }

    private static Kubernetes CreateKubernetes(string host) =>
        new(new KubernetesClientConfiguration
        {
            Host = $"http://{host}"
        });

    private sealed class RecordingTwoTargetProvider(
        IReadOnlyDictionary<string, IKubernetes> clients)
        : IKubernetesClientProvider
    {
        public List<string> RequestedTargetIds { get; } = [];
        public int DefaultRequestCount { get; private set; }

        public Task<IKubernetes> GetAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default)
        {
            RequestedTargetIds.Add(deploymentTargetId);
            return Task.FromResult(clients[deploymentTargetId]);
        }

        public Task<IKubernetes> GetDefaultAsync(
            CancellationToken cancellationToken = default)
        {
            DefaultRequestCount++;
            throw new InvalidOperationException(
                "Placement-aware reconciliation must not request a default client.");
        }
    }

    private sealed class RecordingAdapter : IResourceDeploymentAdapter
    {
        public IKubernetes? Kubernetes { get; private set; }
        public string? DeploymentTargetId { get; private set; }

        public Task<ResourceDeploymentAdapterResult> ApplyAsync(
            ResourceDeploymentReconciliationContext context,
            CancellationToken cancellationToken = default)
        {
            Kubernetes = context.Kubernetes;
            DeploymentTargetId =
                context.Deployment.ResolvedPlacement.DeploymentTargetId;
            return Task.FromResult<ResourceDeploymentAdapterResult>(
                ResourceDeploymentAdapterResult.Succeeded.Create(
                [
                    RedisResourceDeploymentEndpoint.Create(
                        "redis.pole-routing-test.svc.cluster.local",
                        6379)
                ]));
        }

        public Task<ResourceDeploymentAdapterResult> DeleteAsync(
            ResourceDeploymentReconciliationContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ResourceDeploymentAdapterResult>(
                ResourceDeploymentAdapterResult.Succeeded.Create());
    }

    private sealed class ThrowingTransportAdapter : IResourceDeploymentAdapter
    {
        public Task<ResourceDeploymentAdapterResult> ApplyAsync(
            ResourceDeploymentReconciliationContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ResourceDeploymentAdapterResult>(
                new HttpRequestException("Synthetic Kubernetes transport failure."));

        public Task<ResourceDeploymentAdapterResult> DeleteAsync(
            ResourceDeploymentReconciliationContext context,
            CancellationToken cancellationToken = default) =>
            ApplyAsync(context, cancellationToken);
    }
}
