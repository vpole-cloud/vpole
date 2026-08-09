using k8s;
using Microsoft.FSharp.Core;
using MongoDB.Bson;
using Pole.Features.Core.Kubernetes;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Application.Kubernetes;
using Pole.Features.Postgres.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.ResultType;
using Xunit;

namespace Pole.Tests.Integration.Postgres;

public sealed class PostgresResourceDeploymentAdapterTests
{
    [Fact]
    public async Task Apply_UsesPersistedTargetPlacementAndCompute()
    {
        var cluster = CreateCluster(PostgresDeploymentMode.StatefulSet);
        var reader = new RecordingReader(cluster);
        var credentials = new RecordingCredentialService();
        var manager = new RecordingDeploymentManager();
        using var kubernetes = CreateKubernetes();
        var provider = new RecordingKubernetesClientProvider(kubernetes);
        IResourceDeploymentAdapter subject = new PostgresResourceDeploymentAdapter(
            reader,
            credentials,
            manager,
            provider);

        var result = await subject.ApplyAsync(CreateContext());

        var succeeded = Assert.IsType<ResourceDeploymentAdapterResult.Succeeded>(result);
        var endpoint = Assert.Single(succeeded.Endpoints);
        Assert.Equal("PostgreSql", endpoint.Kind);
        Assert.Equal(
            $"{ResourceId}-rw.{KubernetesNamespace}.svc.cluster.local",
            endpoint.Host);
        Assert.Equal(5432, endpoint.Port);
        Assert.Equal([TargetId], provider.RequestedTargetIds);
        Assert.Equal([(TenantId, ResourceId)], reader.Requests);
        Assert.Equal([(TenantId, ResourceId)], credentials.ResolveRequests);

        var apply = Assert.Single(manager.ApplyRequests);
        Assert.Same(kubernetes, apply.Kubernetes);
        Assert.Same(cluster, apply.Cluster);
        Assert.Equal(KubernetesNamespace, apply.NamespaceName);
        Assert.Equal(StorageClassName, apply.StorageClassName);
        Assert.Equal(250, apply.Compute.Requests.CpuMillis);
        Assert.Equal(1024, apply.Compute.Limits.MemoryMi);
        Assert.Equal(TargetId, apply.Metadata.DeploymentTargetId);
        Assert.Equal(7, apply.Metadata.Generation);
    }

    [Fact]
    public async Task Apply_OperatorWithRuntimeClass_FailsBeforeCredentialsOrTargetLookup()
    {
        var reader = new RecordingReader(CreateCluster(PostgresDeploymentMode.Operator));
        var credentials = new RecordingCredentialService();
        var manager = new RecordingDeploymentManager();
        using var kubernetes = CreateKubernetes();
        var provider = new RecordingKubernetesClientProvider(kubernetes);
        IResourceDeploymentAdapter subject = new PostgresResourceDeploymentAdapter(
            reader,
            credentials,
            manager,
            provider);

        var result = await subject.ApplyAsync(CreateContext(runtimeClassName: "kata"));

        var failed = Assert.IsType<ResourceDeploymentAdapterResult.Failed>(result);
        Assert.Equal("ComputeRuntimeClassUnsupported", failed.FailureCode);
        Assert.False(failed.Retryable);
        Assert.Empty(credentials.ResolveRequests);
        Assert.Empty(provider.RequestedTargetIds);
        Assert.Empty(manager.ApplyRequests);
    }

    [Fact]
    public async Task Delete_UsesPersistedTargetAndNamespace()
    {
        var cluster = CreateCluster(PostgresDeploymentMode.StatefulSet);
        var reader = new RecordingReader(cluster);
        var manager = new RecordingDeploymentManager();
        using var kubernetes = CreateKubernetes();
        var provider = new RecordingKubernetesClientProvider(kubernetes);
        IResourceDeploymentAdapter subject = new PostgresResourceDeploymentAdapter(
            reader,
            new RecordingCredentialService(),
            manager,
            provider);

        var result = await subject.DeleteAsync(CreateContext());

        var succeeded = Assert.IsType<ResourceDeploymentAdapterResult.Succeeded>(result);
        Assert.Empty(succeeded.Endpoints);
        Assert.Equal([TargetId], provider.RequestedTargetIds);
        var delete = Assert.Single(manager.DeleteRequests);
        Assert.Same(kubernetes, delete.Kubernetes);
        Assert.Equal(ResourceId, delete.ClusterId);
        Assert.Equal(KubernetesNamespace, delete.NamespaceName);
        Assert.Equal(PostgresDeploymentMode.StatefulSet, delete.DeploymentMode);
    }

    [Fact]
    public async Task Apply_WithoutStorage_FailsBeforeLogicalOrTargetLookup()
    {
        var reader = new RecordingReader(CreateCluster(PostgresDeploymentMode.StatefulSet));
        using var kubernetes = CreateKubernetes();
        var provider = new RecordingKubernetesClientProvider(kubernetes);
        IResourceDeploymentAdapter subject = new PostgresResourceDeploymentAdapter(
            reader,
            new RecordingCredentialService(),
            new RecordingDeploymentManager(),
            provider);

        var result = await subject.ApplyAsync(CreateContext(includeStorage: false));

        var failed = Assert.IsType<ResourceDeploymentAdapterResult.Failed>(result);
        Assert.Equal("StorageConfigurationMissing", failed.FailureCode);
        Assert.False(failed.Retryable);
        Assert.Empty(reader.Requests);
        Assert.Empty(provider.RequestedTargetIds);
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
            ResourceDeploymentComputeArchitecture.Any,
            [],
            [],
            [],
            [],
            [],
            runtimeClassName);
        var placement = new ResourceDeploymentResolvedPlacementSnapshot(
            TargetId,
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
                DesiredGeneration: 7,
                UpdatedAtUtc: NowUtc,
                placement));
    }

    private static PostgresCluster CreateCluster(PostgresDeploymentMode mode) =>
        PostgresCluster.Create(
            TenantId,
            "analytics",
            ResourceGroupId,
            17,
            mode,
            StorageProfileId,
            new CreatePostgresClusterOptions(
                FSharpOption<string>.Some(ResourceId),
                1,
                20));

    private static Kubernetes CreateKubernetes() =>
        new(new KubernetesClientConfiguration
        {
            Host = "http://persisted-target.test"
        });

    private sealed class RecordingReader(PostgresCluster cluster)
        : IPostgresResourceDeploymentReader
    {
        public List<(string TenantId, string ClusterId)> Requests { get; } = [];

        public Task<FSharpOption<PostgresCluster>> FindAsync(
            string tenantId,
            string clusterId,
            CancellationToken cancellationToken)
        {
            Requests.Add((tenantId, clusterId));
            return Task.FromResult(FSharpOption<PostgresCluster>.Some(cluster));
        }
    }

    private sealed class RecordingCredentialService : IPostgresCredentialService
    {
        public List<(string TenantId, string ClusterId)> ResolveRequests { get; } = [];

        public Task<Result<PostgresCredential>> CreateAsync(
            string tenantId,
            string clusterId,
            string username,
            string password,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<PostgresCredential>> RotateAsync(
            PostgresCredential credential,
            string password,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<PostgresClusterCredentials>> ResolveAsync(
            string tenantId,
            string clusterId,
            CancellationToken cancellationToken)
        {
            ResolveRequests.Add((tenantId, clusterId));
            Result<PostgresClusterCredentials> result =
                new PostgresClusterCredentials("postgres", "secret");
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingKubernetesClientProvider(IKubernetes kubernetes)
        : IKubernetesClientProvider
    {
        public List<string> RequestedTargetIds { get; } = [];

        public Task<IKubernetes> GetAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default)
        {
            RequestedTargetIds.Add(deploymentTargetId);
            return Task.FromResult(kubernetes);
        }

        public Task<IKubernetes> GetDefaultAsync(
            CancellationToken cancellationToken = default) =>
            throw new Xunit.Sdk.XunitException(
                "Placement-aware PostgreSQL must not request the default Kubernetes client.");
    }

    private sealed class RecordingDeploymentManager : IPostgresClusterDeploymentManager
    {
        public List<ApplyRequest> ApplyRequests { get; } = [];
        public List<DeleteRequest> DeleteRequests { get; } = [];

        public Task ApplyAsync(
            IKubernetes kubernetes,
            PostgresCluster cluster,
            PostgresClusterCredentials credentials,
            string namespaceName,
            string storageClassName,
            KubernetesComputeConfiguration compute,
            PostgresResourceDeploymentMetadata metadata,
            CancellationToken cancellationToken)
        {
            ApplyRequests.Add(new ApplyRequest(
                kubernetes,
                cluster,
                credentials,
                namespaceName,
                storageClassName,
                compute,
                metadata));
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            IKubernetes kubernetes,
            string clusterId,
            string namespaceName,
            PostgresDeploymentMode deploymentMode,
            CancellationToken cancellationToken)
        {
            DeleteRequests.Add(new DeleteRequest(
                kubernetes,
                clusterId,
                namespaceName,
                deploymentMode));
            return Task.CompletedTask;
        }
    }

    private sealed record ApplyRequest(
        IKubernetes Kubernetes,
        PostgresCluster Cluster,
        PostgresClusterCredentials Credentials,
        string NamespaceName,
        string StorageClassName,
        KubernetesComputeConfiguration Compute,
        PostgresResourceDeploymentMetadata Metadata);

    private sealed record DeleteRequest(
        IKubernetes Kubernetes,
        string ClusterId,
        string NamespaceName,
        PostgresDeploymentMode DeploymentMode);

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
    private static readonly DateTime NowUtc =
        new(2026, 8, 9, 14, 0, 0, DateTimeKind.Utc);
}
