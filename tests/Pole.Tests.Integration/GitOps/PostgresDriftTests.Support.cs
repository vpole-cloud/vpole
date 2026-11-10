using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Compiler;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Domain;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.Secrets.Domain;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class DeclarativePipelineTestBase
{
    private protected IMongoCollection<PostgresCluster> PostgresResources =>
        Fixture.Database.GetCollection<PostgresCluster>("Postgres");

    private protected async Task SeedPostgresPlanningCatalog(
        PostgresDeploymentMode mode = PostgresDeploymentMode.StatefulSet)
    {
        Services.GetRequiredService<PostgresSettings>().DefaultDeploymentMode = mode;
        await Fixture.PrepareNamedCatalogAsync();
    }

    private protected static ManifestFile[] PostgresSnapshot(
        int version = 16,
        string username = "application",
        int size = 4,
        int instances = 1,
        string? scope = "private-data",
        long storageRevision = 1) =>
    [
        new(
            "postgres.yaml",
            $$"""
              apiVersion: pole.io/v1alpha1
              kind: PostgresCluster
              metadata:
                name: database
              spec:
                resourceGroup: postgres-group
                placement:
                  deploymentTarget: lab
                  computeProfile: { name: small, revision: 1 }
                  {{(scope is null ? "" : $"isolationScope: {scope}")}}
                storage:
                  sizeGb: {{size}}
                  profile: { name: postgres-storage, revision: {{storageRevision}} }
                version: {{version}}
                instances: {{instances}}
                username: {{username}}
                bootstrapPassword: { secret: database-auth, key: password }
              ---
              apiVersion: pole.io/v1alpha1
              kind: SecretBinding
              metadata:
                name: database-bootstrap
              spec:
                secret: database-auth
                consumer: {kind: PostgresCluster, name: database}
                usage: Bootstrap
              ---
              apiVersion: pole.io/v1alpha1
              kind: Secret
              metadata:
                name: database-auth
              spec:
                resourceGroup: postgres-group
                {{(scope is null ? "" : $"isolationScope: {scope}")}}
                keys: [password]
              ---
              apiVersion: pole.io/v1alpha1
              kind: IsolationScope
              metadata:
                name: private-data
              spec:
                resourceGroup: postgres-group
              ---
              apiVersion: pole.io/v1alpha1
              kind: ResourceGroup
              metadata:
                name: postgres-group
              spec:
                description: PostgreSQL group
              """),
    ];

    private protected async Task<PostgresCluster> InitializePostgres(ManifestFile[]? files = null)
    {
        files ??= PostgresSnapshot();
        var blocked = await Apply(files);
        Assert.Equal(RepositoryPipelineStatus.Blocked, blocked.Status);
        var prepared = await PostgresResources.Find(ScenarioFilter<PostgresCluster>(item => item.TenantId))
            .SingleAsync();
        Assert.True(prepared.IsPrepared);
        var placement = await DbContext.ResourceDeployments.Find(item => item.ResourceId == prepared.Id).SingleAsync();
        Assert.IsType<PreparedResourceDeploymentState>(placement.State);
        Assert.Equal(1, await Secrets.CountDocumentsAsync(ScenarioFilter<Secret>(item => item.TenantId)));
        await Supply("initial-private-value");
        var applied = await Apply(files);
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, applied.Status);
        return await PostgresResources.Find(item => item.Id == prepared.Id).SingleAsync();
    }
}
