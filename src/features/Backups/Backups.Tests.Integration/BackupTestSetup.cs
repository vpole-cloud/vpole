using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Backups.Application;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Events;
using Pole.Features.Backups.Migrations;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceConnections.Application;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Primitives;
using Pole.Features.StorageProfiles.Domain;
using Pole.Features.StorageProfiles.Primitives;
using Pole.Platform.Hosting;
using Pole.Testing;
using Pole.Workloads;
using Xunit;

namespace Pole.Features.Backups.Tests.Integration;

internal sealed class BackupTestSetup(
    MongoReplicaSetFixture mongo
)
{
    internal async Task SeedSourceAsync(
        IServiceProvider services,
        BackupDestinationInput input,
        ResourceTypeId type,
        string id,
        string deploymentId)
    {
        const string target = TestPlatformDefaults.DeploymentTargetId;
        const string tenant = TestPlatformDefaults.TenantId;
        var computeId = ObjectId.GenerateNewId().ToString();
        var storageId = ObjectId.GenerateNewId().ToString();
        var compute = ComputeProfileFactory.Create(
            "database-" + computeId,
            "Database",
            "Source workload only",
            ComputeProfileResourceRequirements.Create(
                ComputeProfileResourceQuantity.Create(250, 256),
                ComputeProfileResourceQuantity.Create(500, 512)),
            ComputeArchitecture.Any,
            [],
            [type],
            AllTenantsComputeProfileAccess.Create(),
            [ComputeProfileTargetMapping.Create(target)],
            true,
            false,
            new ComputeProfileFactory.CreateOptions(computeId, DateTime.UtcNow));
        await mongo.Database.GetCollection<ComputeProfile>("ComputeProfiles").InsertOneAsync(compute);
        var storage = StorageProfileFactory.Create(
            "disk-" + storageId,
            "Disk",
            "Database storage",
            StorageMediaType.SSD,
            StoragePerformanceTier.Standard,
            true,
            false,
            [StorageProfileTargetMapping.Create(target, "standard")],
            [type],
            StorageProfileTenantAccess.Create(StorageProfileTenantAccessMode.AllTenants, []),
            StorageProfileSizeLimits.Create(1, 100),
            StorageProfileFactory.DefaultOptions with { Id = storageId });
        await mongo.Database.GetCollection<StorageProfile>("StorageProfiles").InsertOneAsync(storage);
        await services.GetRequiredService<MongoRunTransaction>()
            .InvokeOutcomeAsync<bool>(async (tx, ct) =>
            {
                var registration = await services.GetRequiredService<IResourceRegistrar>()
                    .RegisterAsync(new(tenant, input.ResourceGroupId, new(type, id, "source-" + id)), tx.Session, ct);
                Assert.IsType<RegisteredResource>(registration.Value);
                var placement = await services.GetRequiredService<CreateResourceDeployment>()(
                    new(
                        deploymentId,
                        tenant,
                        type,
                        id,
                        input.ResourceGroupId,
                        input.IsolationScopeId,
                        new(target, 1, null),
                        new(
                            computeId,
                            1,
                            target,
                            "Database",
                            new(250, 256),
                            new(500, 512),
                            ComputeArchitecture.Any,
                            [],
                            [],
                            [],
                            [],
                            [],
                            null),
                        new(storageId, 1, "standard"),
                        ResourcePlacementCapability.SingleTarget),
                    tx.Session,
                    ct);
                Assert.IsType<PlacementSummaryDto>(placement.Value);
                return tx.Commit(true);
            });
    }

    internal async Task<FeatureTestHost> StartAsync(
        bool failDestination = false,
        bool failOperation = false,
        bool failPolicy = false,
        Func<bool>? failHistoryContinuation = null)
    {
        var host = await FeatureTestHost.StartAsync(
            new ConfigurationBuilder().Build(),
            typeof(BackupsFeature).Assembly,
            typeof(BackupConfigurationChangedEvent).Assembly,
            (services, _) => services.AddBackupsFeature().AddResourceConnectionsFeature(),
            endpoints => endpoints.MapBackupsEndpoints(),
            configureServices: services =>
            {
                services.AddTestDeploymentTargets();
                // The feature host has no Kubernetes cluster. Simulate only reconciliation of its Secret projection.
                services.AddSingleton<EnsureConsumerSecretProjections>(_ => (request, _) => Task.FromResult(
                    new EnsureConsumerSecretProjectionsOutcome(
                        new ConsumerSecretProjectionKeys(
                        [
                            .. request.References.Select(key => new ConsumerSecretProjectionKey(
                                key.SecretId,
                                key.Key,
                                "storage-credentials",
                                key.Key,
                                1))
                        ]))));
                services.AddSingleton<TestBackupProvider>();
                services.AddSingleton<IBackupProvider>(sp => sp.GetRequiredService<TestBackupProvider>());
                services.AddSingleton(
                    new SecretDeclarationConsumer(
                        "IndependentArchiveReader",
                        ResourceTypeId.Create("tests/archive-reader"),
                        SecretBindingUsage.BackupStorage,
                        SecretConsumerProjectionPolicy.DeclaredPurposeForAllDeployments,
                        SecretConsumerPlacementRequirement.AllowBeforePlacement,
                        SecretConsumerValueAccess.GrantedKeys));
                services.AddSingleton(mongo.Client);
                services.AddSingleton(mongo.Database);
                services.AddFeatureMigrations("pole.backups", typeof(BackupsIndexesMigration).Assembly);
                if (failDestination || failOperation || failPolicy || failHistoryContinuation is not null)
                {
                    services.RemoveAll<StoreOutboxWrites>();
                    services.AddSingleton<StoreOutboxWrites>(_ => async (
                        session, writes,
                        ct) =>
                    {
                        await mongo.StoreOutboxWrites(session, writes, ct);
                        if (writes.Any(w => failDestination && w.Event is BackupConfigurationChangedEvent ||
                                failOperation && w.Event is BackupOperationWorkRequestedEvent ||
                                failPolicy && w.Event is BackupPolicyWorkRequestedEvent ||
                                failHistoryContinuation?.Invoke() == true &&
                                w.Event is BackupHistoryWorkRequestedEvent))
                            throw new InvalidOperationException("Injected transactional outbox failure.");
                    });
                }
            });
        await host.SeedTenantAsync();
        return host;
    }

    internal static HttpClient Client(FeatureTestHost host, string tenant)
    {
        var client = host.GetClient();
        client.DefaultRequestHeaders.Add(FeatureTestAuthentication.UserHeader, TestPlatformDefaults.UserId);
        client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, tenant);
        return client;
    }

    internal static async Task<BackupDestinationInput> StorageAsync(HttpClient client)
    {
        using var groupResponse = await client.PostAsJsonAsync(
            "/api/v1/resource-groups/",
            new { name = "backup-tests", description = "", tags = Array.Empty<string>() });
        groupResponse.EnsureSuccessStatusCode();
        var group = await groupResponse.Content.ReadFromJsonAsync<JsonElement>();
        var groupId = group.GetProperty("id").GetString()!;
        var scope = group.GetProperty("defaultIsolationScopeId").GetString()!;
        using var secretResponse = await client.PostAsJsonAsync(
            "/api/v1/secrets/",
            new
            {
                resourceGroupId = groupId,
                isolationScopeId = scope, name = "storage", purpose = "Backup credentials",
                values = new { id = "catalog-id", key = "catalog-private-value" }
            });
        secretResponse.EnsureSuccessStatusCode();
        var secret = await secretResponse.Content.ReadFromJsonAsync<JsonElement>();
        return new(
            "snapshots",
            groupId,
            scope,
            "https://storage.example",
            "service-backups",
            "",
            secret.GetProperty("id").GetString()!,
            "id",
            "key");
    }
}
