using k8s;
using k8s.Models;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application.Bootstrap;
using Pole.Features.Secrets.Application.Declarations;
using Pole.Features.Secrets.Application.Lifecycle;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Application.References;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Workloads;

namespace Pole.Features.Postgres.Tests.Integration.Infrastructure;

/// <summary>Real Secrets projection target with controlled authority/placement and a live cluster.</summary>
internal sealed class ManagedProjectionFixture : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly IServiceScope _scope;
    private readonly SecretBinding _binding;
    private readonly PostgresDeploymentMode _mode;
    private Secret? _authority;
    private bool _applied;
    public SecretProjection Projection { get; private set; }

    public ManagedProjectionFixture(IKubernetes kubernetes, PostgresCluster cluster, string namespaceName)
    {
        _mode = cluster.Spec.DeploymentMode;
        _binding = SecretBinding.Create(Id(), "pole/postgresql", cluster.Id, new AllSecretDeployments(), []);
        _authority = SecretFactory.CreateManaged(Id(), cluster.TenantId, cluster.GroupId, Id(), "managed-postgres", "credentials",
            ["username", "password"], _binding, KubernetesSecretType.BasicAuthentication, DateTime.UtcNow);
        Projection = SecretProjectionFactory.Create(_authority, _binding, Id(), Id(), PostgresTestData.DefaultDeploymentTargetId, namespaceName);
        var services = new ServiceCollection();
        services.AddSecretsFeature();
        services.AddSingleton<IKubernetesClientProvider>(new SingleTargetProvider(kubernetes, Projection.DeploymentTargetId));
        services.AddScoped<LoadSecretById>(_ => (tenant, id, ct) => Task.FromResult(
            _authority is { } authority && authority.TenantId == tenant && authority.Id == id ? authority : null));
        services.AddScoped<LoadExactResourceDeploymentRuntimePlacement>(_ => (request, ct) =>
            Task.FromResult<ResourceDeploymentRuntimePlacementDto?>(
                request.TenantId == Projection.TenantId
                    && request.ResourceType == ResourceTypeId.Create("pole/postgresql") &&
                request.ResourceId == Projection.ConsumerId && request.DeploymentId == Projection.DeploymentId
                    ? new ResourceDeploymentRuntimePlacementDto(Projection.DeploymentId, Projection.ConsumerId, Projection.RuntimeCellId,
                        Projection.DeploymentTargetId, Projection.KubernetesNamespace, 1, 1,
                        new ResourceDeploymentComputeConfiguration(PostgresTestData.DefaultComputeProfileId, 1,
                            Projection.DeploymentTargetId, "test", new(250, 256), new(1000, 1024), ComputeArchitecture.Any,
                            [], [], [], [], [], null), ResourceDeploymentStatus.Applied,
                                ResourceDeploymentDesiredOperation.Apply, Projection.IsolationScopeId)
                    : null));
        _services = services.BuildServiceProvider(validateScopes: true);
        _scope = _services.CreateScope();
    }

    public PostgresProjectedCredentials Credentials => _mode == PostgresDeploymentMode.Operator
        ? PostgresProjectedCredentials.NewOperatorCredentials("postgres-user", Reference)
        : PostgresProjectedCredentials.NewStatefulSetCredentials(Reference);

    private SecretProjectionReference Reference => new(Projection.SecretId, Projection.KubernetesName, Projection.DesiredVersion);

    public async Task ApplyAsync(string password)
    {
        if (_applied)
            _authority = _authority!.Rotate(["username", "password"], DateTime.UtcNow);
        Projection = SecretProjectionFactory.Create(_authority!, _binding, Projection.DeploymentId, Projection.RuntimeCellId,
            Projection.DeploymentTargetId, Projection.KubernetesNamespace);
        var applied = await Target.ApplyAsync(Projection, _ => Task.FromResult<SecretProjectionManifestOutcome>(SecretProjectionManifest.Create(Projection, _binding,
            new Dictionary<string, string> { ["username"] = "postgres-user", ["password"] = password })), default);
        Xunit.Assert.IsType<SecretProjectionTargetSucceeded>(applied.Value);
        _applied = true;
    }

    public async Task ReleaseAsync()
    {
        _authority = null;
        Xunit.Assert.IsType<SecretProjectionTargetSucceeded>((await Target.DeleteAsync(Projection, default)).Value);
    }

    private ISecretProjectionTarget Target => _scope.ServiceProvider.GetRequiredService<ISecretProjectionTarget>();
    private static string Id() => ObjectId.GenerateNewId().ToString();
    public void Dispose()
    {
        _scope.Dispose();
        _services.Dispose();
    }

    private sealed class SingleTargetProvider(IKubernetes kubernetes, string target) : IKubernetesClientProvider
    {
        public Task<IKubernetes> GetAsync(string deploymentTargetId, CancellationToken cancellationToken = default)
        {
            Xunit.Assert.Equal(target, deploymentTargetId);
            return Task.FromResult(kubernetes);
        }
        public Task<IKubernetes> GetForCleanupAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default) =>
            GetAsync(deploymentTargetId, cancellationToken);

        public Task<IKubernetes> GetDefaultAsync(CancellationToken cancellationToken = default) =>
            throw new Xunit.Sdk.XunitException("Secret projection must use persisted target.");
    }
}
