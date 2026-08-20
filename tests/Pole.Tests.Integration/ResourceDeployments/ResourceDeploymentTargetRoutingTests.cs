using k8s;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Persistence;
using Pole.Features.ResourceDeployments.Application.Reconciliation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Application.Services;
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
            ResourceDeploymentTestData.Compute(),
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
        var adapter = new RecordingAdapter(provider);
        var settings = new ResourceDeploymentSettings
        {
            ClaimLeaseSeconds = 30,
            InitialRetryDelaySeconds = 1,
            MaximumRetryDelaySeconds = 10
        };
        using var metrics = new ResourceDeploymentMetrics();
        var persistence = CreateReconciliationPersistence(settings);
        var reconciler = new ResourceDeploymentReconciler(
            persistence.ClaimAsync,
            persistence.PersistSuccessAsync,
            persistence.PersistFailureAsync,
            new ResourcePlacementLookup(fixture.DbContext),
            _ => (adapter, ResourcePlacementCapability.SingleTarget),
            metrics,
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
                ResourceDeploymentTestData.Compute(),
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
        var adapter = new ThrowingTransportAdapter(provider);
        var settings = new ResourceDeploymentSettings
        {
            ClaimLeaseSeconds = 30,
            InitialRetryDelaySeconds = 1,
            MaximumRetryDelaySeconds = 10
        };
        using var metrics = new ResourceDeploymentMetrics();
        var persistence = CreateReconciliationPersistence(settings);
        var reconciler = new ResourceDeploymentReconciler(
            persistence.ClaimAsync,
            persistence.PersistSuccessAsync,
            persistence.PersistFailureAsync,
            new ResourcePlacementLookup(fixture.DbContext),
            _ => (adapter, ResourcePlacementCapability.SingleTarget),
            metrics,
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

    private ResourceDeploymentReconciliationPersistence
        CreateReconciliationPersistence(ResourceDeploymentSettings settings) =>
        new(
            fixture.DbContext.ResourceDeployments,
            (operation, cancellationToken) => fixture.DbContext
                .RunInTransactionAsync(
                    operation,
                    cancellationToken: cancellationToken),
            (session, writes, cancellationToken) => fixture.DbContext.StoreEventAsync(
                writes,
                session,
                cancellationToken),
            settings,
            new ResourceDeploymentRetryDelayPolicy(settings),
            TimeProvider.System);

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

    private sealed class RecordingAdapter(IKubernetesClientProvider kubernetesClientProvider)
        : IResourceDeploymentAdapter
    {
        public IKubernetes? Kubernetes { get; private set; }
        public string? DeploymentTargetId { get; private set; }

        public async Task<ResourceDeploymentAdapterResult> ApplyAsync(
            ResourceDeploymentReconciliationContext context,
            CancellationToken cancellationToken = default)
        {
            Kubernetes = await kubernetesClientProvider.GetAsync(
                context.Deployment.Placement.DeploymentTargetId,
                cancellationToken);
            DeploymentTargetId =
                context.Deployment.Placement.DeploymentTargetId;
            return ResourceDeploymentAdapterResult.Succeeded.Create(
            [
                ResourceDeploymentEndpointDto.Create(
                    "Redis",
                    "redis.pole-routing-test.svc.cluster.local",
                    6379,
                    "redis")
            ]);
        }

        public Task<ResourceDeploymentAdapterResult> DeleteAsync(
            ResourceDeploymentReconciliationContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ResourceDeploymentAdapterResult>(
                ResourceDeploymentAdapterResult.Succeeded.Create());
    }

    private sealed class ThrowingTransportAdapter(
        IKubernetesClientProvider kubernetesClientProvider)
        : IResourceDeploymentAdapter
    {
        public async Task<ResourceDeploymentAdapterResult> ApplyAsync(
            ResourceDeploymentReconciliationContext context,
            CancellationToken cancellationToken = default)
        {
            _ = await kubernetesClientProvider.GetAsync(
                context.Deployment.Placement.DeploymentTargetId,
                cancellationToken);
            throw new HttpRequestException("Synthetic Kubernetes transport failure.");
        }

        public Task<ResourceDeploymentAdapterResult> DeleteAsync(
            ResourceDeploymentReconciliationContext context,
            CancellationToken cancellationToken = default) =>
            ApplyAsync(context, cancellationToken);
    }
}
