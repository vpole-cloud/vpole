using k8s;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Application;
using Pole.Features.ResourceDeployments.Application.Admission;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Mongo;
using Pole.Features.ResourceDeployments.Application.Reconciliation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.DeploymentTargets;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

[Collection(IntegrationTestCollection.Global)]
public sealed class ResourceDeploymentTargetRoutingTests(
    MongoReplicaSetFixture fixture) : MongoIntegrationTestBase(fixture)
{
    private ServiceProvider? serviceProvider;
    private IServiceScope? serviceScope;
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

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        serviceProvider = TestServiceProviderFactory.Create(Fixture);
        serviceScope = serviceProvider.CreateScope();
    }

    public override Task DisposeAsync()
    {
        serviceScope?.Dispose();
        serviceProvider?.Dispose();
        return base.DisposeAsync();
    }

    [Theory]
    [InlineData(NetworkPolicyReadiness.EnforcementAttested, true, true, true, false)]
    [InlineData(NetworkPolicyReadiness.IsolationNotEnforced, true, true, true, false)]
    [InlineData(NetworkPolicyReadiness.EnforcementRequired, true, true, false, false)]
    [InlineData(NetworkPolicyReadiness.EnforcementAttested, false, true, false, false)]
    [InlineData(NetworkPolicyReadiness.EnforcementAttested, true, false, false, false)]
    [InlineData(NetworkPolicyReadiness.EnforcementAttested, true, true, false, true)]
    public async Task ReconciliationOnTargetB_ChecksCurrentEnforcementBeforeApplying(
        NetworkPolicyReadiness readiness,
        bool baselineReady,
        bool targetEnabled,
        bool allowed,
        bool targetConnectionUnavailable)
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
            ResourceDeploymentFactory.DefaultOptions with
            {
                Id = DeploymentId,
                CreatedAtUtc = now
            });
        await Fixture.DbContext.RuntimeCells.InsertOneAsync(runtimeCell);
        await Fixture.DbContext.ResourceDeployments.InsertOneAsync(deployment);

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
        var persistence = CreateReconciliationCapabilities(settings);
        var baselineCalls = 0;
        var reconciler = new ResourceDeploymentReconciler(
            persistence.Claim,
            persistence.PersistSuccess,
            persistence.PersistFailure,
            Services.GetRequiredService<LoadResourcePlacement>(),
            _ => (adapter, ResourcePlacementCapability.SingleTarget),
            (id, ct) => LookupTarget(id, ct, readiness, targetEnabled),
(request, ct) =>
            {
                baselineCalls++;
                return Task.FromResult<EnsureRuntimeCellIngressBaselineOutcome>(
                    targetConnectionUnavailable
                        ? new RuntimeCellIngressBaselineTargetConnectionUnavailable(request.RuntimeCellId)
                        : baselineReady
                            ? new RuntimeCellIngressBaselineReady()
                            : new RuntimeCellIngressBaselinePolicyUnavailable(request.RuntimeCellId));
            },
            metrics,
            NullLogger<ResourceDeploymentReconciler>.Instance);

        await reconciler.HandleAsync(
            TenantId,
            DeploymentId,
            RuntimeCellId,
            generation: 1,
            ResourceDeploymentOperation.Apply);

        if (!allowed)
        {
            Assert.Empty(provider.RequestedTargetIds);
            var blocked = await Fixture.DbContext.ResourceDeployments.Find(item => item.Id == DeploymentId)
                .SingleAsync();
            Assert.Equal(
                !targetEnabled
                    ? "DeploymentTargetDisabled"
                    : readiness == NetworkPolicyReadiness.EnforcementRequired
                        ? "NetworkPolicyEnforcementRequired"
                        : targetConnectionUnavailable
                            ? "DeploymentTargetConnectionUnavailable"
                            : "IngressBaselineUnavailable",
                Assert.IsType<FailedResourceDeploymentState>(blocked.State).FailureCode);
            Assert.Equal(targetEnabled && readiness != NetworkPolicyReadiness.EnforcementRequired ? 1 : 0,
                baselineCalls);
            return;
        }

        Assert.Equal(1, baselineCalls);
        Assert.Equal([TargetBId], provider.RequestedTargetIds);
        Assert.Equal(0, provider.DefaultRequestCount);
        Assert.Same(targetB, adapter.Kubernetes);
        Assert.Equal(TargetBId, adapter.DeploymentTargetId);
        var saved = await Fixture.DbContext.ResourceDeployments
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
            ResourceDeploymentFactory.DefaultOptions with
            {
                Id = DeploymentId,
                CreatedAtUtc = now.AddSeconds(1)
            });
        await Fixture.DbContext.RuntimeCells.InsertOneAsync(runtimeCell);
        await Fixture.DbContext.ResourceDeployments.InsertOneAsync(deployment);

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
        var persistence = CreateReconciliationCapabilities(settings);
        var reconciler = new ResourceDeploymentReconciler(
            persistence.Claim,
            persistence.PersistSuccess,
            persistence.PersistFailure,
            Services.GetRequiredService<LoadResourcePlacement>(),
            _ => (adapter, ResourcePlacementCapability.SingleTarget),
            (id, ct) => LookupTarget(id, ct),
(request, ct) =>
                Task.FromResult<EnsureRuntimeCellIngressBaselineOutcome>(
                    new RuntimeCellIngressBaselineReady()),
            metrics,
            NullLogger<ResourceDeploymentReconciler>.Instance);

        await reconciler.HandleAsync(
            TenantId,
            DeploymentId,
            RuntimeCellId,
            generation: 1,
            ResourceDeploymentOperation.Apply);

        var saved = await Fixture.DbContext.ResourceDeployments
            .Find(item => item.Id == DeploymentId)
            .SingleAsync();
        var failed = Assert.IsType<FailedResourceDeploymentState>(saved.State);
        Assert.True(failed.Retryable);
        Assert.Equal("KubernetesTransportFailed", failed.FailureCode);
        Assert.Equal([TargetBId], provider.RequestedTargetIds);
        Assert.Equal(
            1L,
            await Fixture.DbContext.CountAllOutboxMessagesAsync(
                "pole.resource-deployments/v1/resource-deployment-failed"));
    }

    private static Task<LookupDeploymentTargetOutcome> LookupTarget(
        string id,
        CancellationToken cancellationToken,
        NetworkPolicyReadiness readiness = NetworkPolicyReadiness.EnforcementAttested,
        bool enabled = true) =>
        Task.FromResult<LookupDeploymentTargetOutcome>(
            new LookupDeploymentTargetOutcome(DeploymentTargetNetworkPolicyTests.Target().ToLookupDto() with
            {
                Id = id,
                Enabled = enabled,
                NetworkPolicy = new(
                    NetworkPolicyEnforcementStatus.Unknown,
                    readiness,
                    NetworkPolicyReason.EnforcementNotEstablished,
                    DateTime.UnixEpoch,
                    NetworkPolicyEvidenceSource.OperatorAttestation,
                    false)
            }));

    private ReconciliationCapabilities CreateReconciliationCapabilities(
        ResourceDeploymentSettings settings)
    {
        var completion = new ResourceDeploymentCompletionPersistence(
            Fixture.DbContext.ResourceDeployments,
            (operation, cancellationToken) => Fixture.DbContext
                .RunOutcomeInTransactionAsync(
                    operation,
                    cancellationToken: cancellationToken),
            (session, writes, cancellationToken) => Fixture.StoreOutboxWrites(
                session,
                writes,
                cancellationToken));
        var claim = new ClaimResourceDeploymentOperation(
            Fixture.DbContext.ResourceDeployments,
            settings,
            TimeProvider.System,
            new MongoRunTransaction(Fixture.Client),
            Fixture.StoreOutboxWrites);
        var success = new PersistResourceDeploymentSuccessOperation(
            completion,
            TimeProvider.System);
        var failure = new PersistResourceDeploymentFailureOperation(
            completion,
            new ResourceDeploymentRetryDelayPolicy(settings),
            TimeProvider.System);

        return new ReconciliationCapabilities(
            claim.InvokeAsync,
            success.InvokeAsync,
            failure.InvokeAsync);
    }

    private sealed record ReconciliationCapabilities(
        ClaimResourceDeployment Claim,
        PersistResourceDeploymentSuccess PersistSuccess,
        PersistResourceDeploymentFailure PersistFailure);

    private IServiceProvider Services => serviceScope?.ServiceProvider ??
                                         throw new InvalidOperationException(
                                             "The test service scope was not initialized.");

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

        public Task<IKubernetes> GetForCleanupAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default) =>
            GetAsync(deploymentTargetId, cancellationToken);

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
