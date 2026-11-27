// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using k8s;
using Microsoft.FSharp.Core;
using Pole.Features.Core.Kubernetes;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Application.Kubernetes;
using Pole.Features.Postgres.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Secrets.Contracts;
using Xunit;
using Xunit.Sdk;

namespace Pole.Features.Postgres.Tests;

public sealed class PostgresResourceDeploymentAdapterTests
{
    private const string TenantId = "507f1f77bcf86cd799439011";
    private const string ResourceId = "507f1f77bcf86cd799439012";
    private const string ResourceGroupId = "507f1f77bcf86cd799439013";
    private const string IsolationScopeId = "507f1f77bcf86cd799439014";
    private const string RuntimeCellId = "507f1f77bcf86cd799439015";
    private const string TargetId = "507f1f77bcf86cd799439016";
    private const string DeploymentId = "507f1f77bcf86cd799439017";
    private const string StorageProfileId = "507f1f77bcf86cd799439018";
    private const string ComputeProfileId = "507f1f77bcf86cd799439019";
    private const string KubernetesNamespace = "postgres-target-namespace";
    private const string StorageClassName = "postgres-fast-storage";

    private static readonly DateTime NowUtc = new(2026, 8, 9, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task PreparedIdentityCannotTouchIngressCredentialsOrKubernetes()
    {
        var cluster = PostgresProvisioningTests.Prepared();
        var reader = new RecordingReader(cluster);
        var manager = new RecordingDeploymentManager();
        using var kubernetes = CreateKubernetes();
        var provider = new RecordingKubernetesClientProvider(kubernetes);
        IResourceDeploymentAdapter subject = new PostgresResourceDeploymentAdapter(
            reader.LoadAsync,
            (_, _) => throw new InvalidOperationException("Prepared resources cannot project credentials."),
            manager,
            provider,
            (_, _) => throw new InvalidOperationException("Prepared resources cannot reconcile ingress."));

        var result = Assert.IsType<ResourceDeploymentAdapterResult.Failed>(await subject.ApplyAsync(CreateContext()));
        Assert.Equal("PostgresConfigurationPending", result.FailureCode);
        Assert.Empty(provider.RequestedTargetIds);
        Assert.Empty(manager.ApplyRequests);
    }

    [Fact]
    public async Task Apply_UsesPersistedTargetPlacementAndCompute()
    {
        var cluster = CreateCluster();
        var reader = new RecordingReader(cluster);
        var manager = new RecordingDeploymentManager();
        using var kubernetes = CreateKubernetes();
        var provider = new RecordingKubernetesClientProvider(kubernetes);
        IResourceDeploymentAdapter subject = new PostgresResourceDeploymentAdapter(
            reader.LoadAsync,
            ProjectionReady,
            manager,
            provider,
            AppliedIngress);

        var result = await subject.ApplyAsync(CreateContext());

        var succeeded = Assert.IsType<ResourceDeploymentAdapterResult.Succeeded>(result);
        var endpoint = Assert.Single(succeeded.Endpoints);
        Assert.Equal("PostgreSql", endpoint.Kind);
        Assert.Equal(
            $"{PostgresClusterKubernetesBuilder.buildReadWriteServiceName(ResourceId)}.{KubernetesNamespace}.svc.cluster.local",
            endpoint.Host);
        Assert.Equal(5432, endpoint.Port);
        Assert.Equal([TargetId], provider.RequestedTargetIds);
        Assert.Equal([(TenantId, ResourceId)], reader.Requests);

        var apply = Assert.Single(manager.ApplyRequests);
        Assert.Same(kubernetes, apply.Kubernetes);
        Assert.Same(cluster, apply.Cluster);
        Assert.Equal(KubernetesNamespace, apply.NamespaceName);
        Assert.Equal(StorageClassName, apply.StorageClassName);
        Assert.Equal(250, apply.Compute.Requests.CpuMillis);
        Assert.Equal(1024, apply.Compute.Limits.MemoryMi);
        Assert.Equal(TargetId, apply.Metadata.DeploymentTargetId);
        Assert.Equal(7, apply.Metadata.Generation);
        Assert.Equal("provider-generated-secret", apply.Credentials.KubernetesName);
    }

    [Fact]
    public async Task Apply_OperatorWithRuntimeClass_FailsBeforeCredentialsOrTargetLookup()
    {
        var reader = new RecordingReader(CreateCluster());
        var manager = new RecordingDeploymentManager();
        using var kubernetes = CreateKubernetes();
        var provider = new RecordingKubernetesClientProvider(kubernetes);
        IResourceDeploymentAdapter subject = new PostgresResourceDeploymentAdapter(
            reader.LoadAsync,
            ProjectionReady,
            manager,
            provider,
            AppliedIngress);

        var result = await subject.ApplyAsync(CreateContext(runtimeClassName: "kata"));

        var failed = Assert.IsType<ResourceDeploymentAdapterResult.Failed>(result);
        Assert.Equal("ComputeRuntimeClassUnsupported", failed.FailureCode);
        Assert.False(failed.Retryable);
        Assert.Empty(provider.RequestedTargetIds);
        Assert.Empty(manager.ApplyRequests);
    }

    [Fact]
    public async Task Delete_UsesPersistedTargetAndNamespace()
    {
        var cluster = CreateCluster();
        var reader = new RecordingReader(cluster);
        var manager = new RecordingDeploymentManager();
        using var kubernetes = CreateKubernetes();
        var provider = new RecordingKubernetesClientProvider(kubernetes);
        IResourceDeploymentAdapter subject = new PostgresResourceDeploymentAdapter(
            reader.LoadAsync,
            ProjectionReady,
            manager,
            provider,
            AppliedIngress);

        var result = await subject.DeleteAsync(CreateContext());

        var succeeded = Assert.IsType<ResourceDeploymentAdapterResult.Succeeded>(result);
        Assert.Empty(succeeded.Endpoints);
        Assert.Equal([TargetId], provider.RequestedTargetIds);
        var delete = Assert.Single(manager.DeleteRequests);
        Assert.Same(kubernetes, delete.Kubernetes);
        Assert.Equal(ResourceId, delete.ClusterId);
        Assert.Equal(KubernetesNamespace, delete.NamespaceName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_WithoutLogicalCluster_ClosesBothPolicyPurposesBeforeReturning(bool policyFailure)
    {
        var purposes = new List<string>();
        using var kubernetes = CreateKubernetes();
        var provider = new RecordingKubernetesClientProvider(kubernetes);
        var manager = new RecordingDeploymentManager();
        IResourceDeploymentAdapter subject = new PostgresResourceDeploymentAdapter(
            (
                _, _,
                _) => Task.FromResult<FSharpOption<PostgresCluster>>(null!),
            ProjectionReady,
            manager,
            provider,
            (request, _) =>
            {
                Assert.Empty(request.AllowedTcpPorts);
                purposes.Add(request.Purpose);
                return Task.FromResult<ReconcileResourceIngressPolicyOutcome>(
                    policyFailure && request.Purpose is "postgres-replication"
                        ? new ResourceIngressPolicyUnavailable()
                        : new ResourceIngressPolicyApplied());
            });

        var result = Assert.IsType<ResourceDeploymentAdapterResult.Failed>(await subject.DeleteAsync(CreateContext()));

        Assert.Equal(["postgres-replication", "postgres-operator"], purposes);
        Assert.Equal(
            policyFailure ? "PostgresReplicationIngressCleanupUnavailable" : "PostgresClusterNotFound",
            result.FailureCode);
        Assert.Equal(policyFailure, result.Retryable);
        Assert.Empty(provider.RequestedTargetIds);
        Assert.Empty(manager.DeleteRequests);
    }

    [Fact]
    public async Task Apply_WithoutStorage_FailsBeforeLogicalOrTargetLookup()
    {
        var reader = new RecordingReader(CreateCluster());
        using var kubernetes = CreateKubernetes();
        var provider = new RecordingKubernetesClientProvider(kubernetes);
        IResourceDeploymentAdapter subject = new PostgresResourceDeploymentAdapter(
            reader.LoadAsync,
            ProjectionReady,
            new RecordingDeploymentManager(),
            provider,
            AppliedIngress);

        var result = await subject.ApplyAsync(CreateContext(includeStorage: false));

        var failed = Assert.IsType<ResourceDeploymentAdapterResult.Failed>(result);
        Assert.Equal("StorageConfigurationMissing", failed.FailureCode);
        Assert.False(failed.Retryable);
        Assert.Empty(reader.Requests);
        Assert.Empty(provider.RequestedTargetIds);
    }

    [Fact]
    public async Task Apply_WithoutReadyProjection_IsRetryableAndDoesNotCallWorkloadManager()
    {
        var manager = new RecordingDeploymentManager();
        using var kubernetes = CreateKubernetes();
        var provider = new RecordingKubernetesClientProvider(kubernetes);
        IResourceDeploymentAdapter subject = new PostgresResourceDeploymentAdapter(
            new RecordingReader(CreateCluster()).LoadAsync,
            (request, ct) =>
            {
                Assert.Equal(TenantId, request.TenantId);
                Assert.Equal(new ManagedSecretOwner("pole/postgresql", ResourceId), request.Owner);
                Assert.Equal(DeploymentId, request.DeploymentId);
                return Task.FromResult<EnsureManagedSecretProjectionOutcome>(new ManagedSecretUnavailable());
            },
            manager,
            provider,
            AppliedIngress);
        var result = Assert.IsType<ResourceDeploymentAdapterResult.Failed>(await subject.ApplyAsync(CreateContext()));
        Assert.True(result.Retryable);
        Assert.Equal("PostgresSecretProjectionUnavailable", result.FailureCode);
        Assert.Empty(manager.ApplyRequests);
        Assert.Empty(provider.RequestedTargetIds);
    }

    private static Task<ReconcileResourceIngressPolicyOutcome> AppliedIngress(
        ResourceIngressPolicyRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult<ReconcileResourceIngressPolicyOutcome>(new ResourceIngressPolicyApplied());

    private static Task<EnsureManagedSecretProjectionOutcome> ProjectionReady(
        EnsureManagedSecretProjectionRequest request,
        CancellationToken ct)
    {
        Assert.Equal(TenantId, request.TenantId);
        Assert.Equal(new ManagedSecretOwner("pole/postgresql", ResourceId), request.Owner);
        Assert.Equal(DeploymentId, request.DeploymentId);
        return Task.FromResult<EnsureManagedSecretProjectionOutcome>(
            new SecretProjectionReference("secret-id", "provider-generated-secret", 3));
    }

    private static ResourceDeploymentReconciliationContext CreateContext(
        bool includeStorage = true,
        string? runtimeClassName = null)
    {
        var compute = new ResourceDeploymentComputeConfiguration(
            ComputeProfileId,
            3,
            TargetId,
            "PostgreSQL compute",
            new ResourceDeploymentComputeResourceValues(250, 256),
            new ResourceDeploymentComputeResourceValues(1000, 1024),
            ComputeArchitecture.Any,
            [],
            [],
            [],
            [],
            [],
            runtimeClassName);
        var placement = new ResourceDeploymentResolvedPlacementSnapshot(
            TargetId,
            TargetWorkloadConfigurationRevision: 1,
            KubernetesNamespace,
            Ingress: null,
            includeStorage
                ? new ResourceDeploymentStorageConfiguration(
                    StorageProfileId,
                    StorageProfileRevision: 2,
                    StorageClassName)
                : null,
            compute);
        return new ResourceDeploymentReconciliationContext(
            new ResourceDeploymentReconciliationSnapshot(
                DeploymentId,
                TenantId,
                PostgresResourceDeployment.ResourceType,
                ResourceId,
                ResourceGroupId,
                IsolationScopeId,
                RuntimeCellId,
                ResourceRevision: 1,
                FixedReplicas: 1,
                DesiredGeneration: 7,
                UpdatedAtUtc: NowUtc,
                placement));
    }

    private static PostgresCluster CreateCluster() => PostgresCluster.Create(
        TenantId,
        "analytics",
        ResourceGroupId,
        17,
        StorageProfileId,
        new CreatePostgresClusterOptions(FSharpOption<string>.Some(ResourceId), 1, 20));

    private static Kubernetes CreateKubernetes() => new(
        new KubernetesClientConfiguration
        {
            Host = "http://persisted-target.test",
        });

    private sealed class RecordingReader(
        PostgresCluster cluster
    )
    {
        public List<(string TenantId, string ClusterId)> Requests { get; } = [];

        public Task<FSharpOption<PostgresCluster>> LoadAsync(
            string tenantId,
            string clusterId,
            CancellationToken cancellationToken)
        {
            Requests.Add((tenantId, clusterId));
            return Task.FromResult(FSharpOption<PostgresCluster>.Some(cluster));
        }
    }

    private sealed class RecordingKubernetesClientProvider(
        IKubernetes kubernetes
    ) : IKubernetesClientProvider
    {
        public List<string> RequestedTargetIds { get; } = [];

        public Task<IKubernetes> GetAsync(string deploymentTargetId, CancellationToken cancellationToken = default)
        {
            RequestedTargetIds.Add(deploymentTargetId);
            return Task.FromResult(kubernetes);
        }

        public Task<IKubernetes> GetForCleanupAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default) => GetAsync(deploymentTargetId, cancellationToken);

        public Task<IKubernetes> GetDefaultAsync(CancellationToken cancellationToken = default) =>
            throw new XunitException("Placement-aware PostgreSQL must not request the default Kubernetes client.");
    }

    private sealed class RecordingDeploymentManager : IPostgresClusterDeploymentManager
    {
        public List<ApplyRequest> ApplyRequests { get; } = [];
        public List<DeleteRequest> DeleteRequests { get; } = [];

        public Task ApplyAsync(
            IKubernetes kubernetes,
            PostgresCluster cluster,
            SecretProjectionReference credentials,
            string namespaceName,
            string storageClassName,
            KubernetesComputeConfiguration compute,
            PostgresResourceDeploymentMetadata metadata,
            CancellationToken cancellationToken)
        {
            ApplyRequests.Add(
                new ApplyRequest(kubernetes, cluster, credentials, namespaceName, storageClassName, compute, metadata));
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            IKubernetes kubernetes,
            string clusterId,
            string namespaceName,
            CancellationToken cancellationToken)
        {
            DeleteRequests.Add(new DeleteRequest(kubernetes, clusterId, namespaceName));
            return Task.CompletedTask;
        }
    }

    private sealed record ApplyRequest(
        IKubernetes Kubernetes,
        PostgresCluster Cluster,
        SecretProjectionReference Credentials,
        string NamespaceName,
        string StorageClassName,
        KubernetesComputeConfiguration Compute,
        PostgresResourceDeploymentMetadata Metadata
    );

    private sealed record DeleteRequest(
        IKubernetes Kubernetes,
        string ClusterId,
        string NamespaceName
    );
}
