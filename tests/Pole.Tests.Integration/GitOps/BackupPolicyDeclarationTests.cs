using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.Backups.Domain;
using Pole.Features.GitOps.Compiler;
using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Primitives;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

[Collection(IntegrationTestCollection.Tenant1)]
public sealed class BackupPolicyDeclarationTests(
    MongoReplicaSetFixture fixture
) : DeclarativePipelineTestBase(fixture)
{
    [Fact]
    public async Task
        Policy_in_a_separate_repository_binds_exact_source_and_destination_and_resyncs_without_business_writes()
    {
        await SeedPostgresPlanningCatalog();
        var cluster = await InitializePostgres();
        var backupRepository = Id();
        var files = BackupFiles("archives", moving: false);
        var initial = await Apply(files, backupRepository);
        Assert.Equal(RepositoryPipelineStatus.Blocked, initial.Status);
        var secret = await Secrets.Find(item => item.TenantId == Data.TenantId && item.Name == "storage-keys")
            .SingleAsync();
        var grant = Assert.Single(secret.Bindings);
        Assert.Equal(SecretBindingUsage.BackupStorage, grant.Usage);
        var result = await Services.GetRequiredService<PersistRotatedSecret>()(
            Data.TenantId,
            secret.Id,
            secret.CurrentVersion,
            new Dictionary<string, string> { ["access"] = "test-id", ["secret"] = "test-key" },
            default);
        Assert.IsType<Secret>(result.Value);
        await AssertApplied(files);

        var policies = Fixture.Database.GetCollection<BackupPolicy>("BackupPolicies");
        var archives = Fixture.Database.GetCollection<BackupArchive>("BackupArchives");
        var policy = await policies.Find(p => p.TenantId == Data.TenantId).SingleAsync();
        var archive = await archives.Find(a => a.TenantId == Data.TenantId).SingleAsync();
        Assert.Equal(cluster.Id, policy.ResourceId);
        Assert.Equal(grant.Consumer.ResourceId, policy.DestinationId);
        Assert.Equal(policy.ArchiveId, archive.Id);
        Assert.Equal(secret.Id, archive.Storage.CredentialSecretId);
        Assert.Equal(
            source,
            (await Inventory.Find(d => d.TenantId == Data.TenantId && d.ResourceId == cluster.Id).SingleAsync())
            .SourceId);
        Assert.Equal(
            backupRepository,
            (await Inventory.Find(d => d.TenantId == Data.TenantId && d.ResourceId == policy.Id).SingleAsync())
            .SourceId);

        var events = await BusinessEvents();
        await AssertApplied(files);
        var resynced = await policies.Find(p => p.TenantId == Data.TenantId).SingleAsync();
        Assert.Equal(policy.Revision, resynced.Revision);
        Assert.Equal(policy.ArchiveId, resynced.ArchiveId);
        Assert.Equal(events, await BusinessEvents());

        await AssertApplied(BackupFiles("new-archive", moving: true));
        var moved = await policies.Find(p => p.TenantId == Data.TenantId).SingleAsync();
        Assert.Equal(policy.Id, moved.Id);
        Assert.NotEqual(policy.ArchiveId, moved.ArchiveId);
        var retained = await archives.Find(a => a.TenantId == Data.TenantId).ToListAsync();
        Assert.False(retained.Single(a => a.Id == policy.ArchiveId).Active);
        Assert.True(retained.Single(a => a.Id == moved.ArchiveId).Active);

        async Task AssertApplied(ManifestFile[] snapshot)
        {
            var applied = await Apply(snapshot, backupRepository);
            Assert.True(
                applied.Status == RepositoryPipelineStatus.ConfigurationApplied,
                string.Join(
                    ", ",
                    (await Pipeline.LatestAsync(backupRepository)).Diagnostics.Select(item => item.Code)));
        }
    }

    private static ManifestFile[] BackupFiles(string destination, bool moving) =>
    [
        new(
            "policy.yaml",
            $$"""
              apiVersion: pole.io/v1alpha1
              kind: BackupPolicy
              metadata: { name: nightly }
              spec:
                service: { kind: PostgresCluster, name: database }
                destination: {{destination}}
                schedule: '0 2 * * *'
                retentionDays: 7
              """),
        new(
            "storage-keys.yaml",
            """
            apiVersion: pole.io/v1alpha1
            kind: Secret
            metadata: { name: storage-keys }
            spec:
              resourceGroup: postgres-group
              isolationScope: private-data
              keys: [access, secret]
            """),
        Destination("archives"),
        .. moving ? new[] { Destination(destination) } : Array.Empty<ManifestFile>()
    ];

    private static ManifestFile Destination(string name) => new(
        name + ".yaml",
        $$"""
          apiVersion: pole.io/v1alpha1
          kind: BackupDestination
          metadata: { name: {{name}} }
          spec:
            resourceGroup: postgres-group
            isolationScope: private-data
            endpoint: https://storage.example
            bucket: tenant-db
            prefix: {{name}}
            credentials: { secret: storage-keys, accessKeyIdKey: access, secretAccessKeyKey: secret }
          ---
          apiVersion: pole.io/v1alpha1
          kind: SecretBinding
          metadata: { name: {{name}}-access }
          spec:
            secret: storage-keys
            consumer: { kind: BackupDestination, name: {{name}} }
            usage: BackupStorage
            keyMappings: { access: access, secret: secret }
          """);
}
