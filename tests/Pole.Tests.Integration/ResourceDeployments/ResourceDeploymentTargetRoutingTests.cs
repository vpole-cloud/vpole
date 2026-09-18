using k8s;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Tests.Integration.DeploymentTargets;
using Pole.ResultType;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Persistence;
using Pole.Features.ResourceDeployments.Application.Persistence.Reconciliation;
using Pole.Features.ResourceDeployments.Application.Reconciliation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

[Collection(IntegrationTestCollection.Name)]
public sealed class ResourceDeploymentTargetRoutingTests(
    MongoReplicaSetFixture fixture)
    : IAsyncLifetime
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

    public async Task InitializeAsync()
    {
        await fixture.ResetDatabaseAsync();
        serviceProvider = TestServiceProviderFactory.Create(fixture);
        serviceScope = serviceProvider.CreateScope();
    }

    public Task DisposeAsync()
    {
        serviceScope?.Dispose();
        serviceProvider?.Dispose();
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData("EnforcementAttested", true)]
    [InlineData("IsolationNotEnforced", true)]
    [InlineData("EnforcementRequired", false)]
    public async Task ReconciliationOnTargetB_ChecksCurrentEnforcementBeforeApplying(string readiness, bool allowed)
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
        var persistence = CreateReconciliationCapabilities(settings);
        var reconciler = new ResourceDeploymentReconciler(
            persistence.Claim,
            persistence.PersistSuccess,
            persistence.PersistFailure,
            Services.GetRequiredService<LoadResourcePlacement>(),
            _ => (adapter, ResourcePlacementCapability.SingleTarget),
            (id, ct) => LookupTarget(id, ct, readiness),
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
            var blocked = await fixture.DbContext.ResourceDeployments.Find(item => item.Id == DeploymentId).SingleAsync();
            Assert.Equal("NetworkPolicyEnforcementRequired", Assert.IsType<FailedResourceDeploymentState>(blocked.State).FailureCode);
            return;
        }
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
        var persistence = CreateReconciliationCapabilities(settings);
        var reconciler = new ResourceDeploymentReconciler(
            persistence.Claim,
            persistence.PersistSuccess,
            persistence.PersistFailure,
            Services.GetRequiredService<LoadResourcePlacement>(),
            _ => (adapter, ResourcePlacementCapability.SingleTarget),
            (id, ct) => LookupTarget(id, ct),
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
                "pole.resource-deployments/v1/resource-deployment-failed"));
    }

    private static Task<Result<DeploymentTargetLookupDto, DeploymentTargetLookupError>> LookupTarget(
        string id, CancellationToken cancellationToken, string readiness = "EnforcementAttested") =>
        Task.FromResult<Result<DeploymentTargetLookupDto, DeploymentTargetLookupError>>(
            new Ok<DeploymentTargetLookupDto>(DeploymentTargetNetworkPolicyTests.Target().ToLookupDto() with
            {
                Id = id,
                NetworkPolicy = new("Unknown", readiness, "TestEvidence", DateTime.UnixEpoch, "OperatorAttestation", false)
            }));

    private ReconciliationCapabilities CreateReconciliationCapabilities(
        ResourceDeploymentSettings settings)
    {
        var completion = new ResourceDeploymentCompletionPersistence(
            fixture.DbContext.ResourceDeployments,
            (operation, cancellationToken) => fixture.DbContext
                .RunOutcomeInTransactionAsync(
                    operation,
                    cancellationToken: cancellationToken),
            (session, writes, cancellationToken) => fixture.StoreOutboxWrites(
                session,
                writes,
                cancellationToken));
        var claim = new ClaimResourceDeploymentOperation(
            fixture.DbContext.ResourceDeployments,
            settings,
            TimeProvider.System);
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
        throw new InvalidOperationException("The test service scope was not initialized.");

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
