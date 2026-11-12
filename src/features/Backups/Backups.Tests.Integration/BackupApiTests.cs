using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Backups.Application;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Domain;
using Pole.Features.Backups.Events;
using Pole.Features.Tenants.Domain;
using Pole.Features.Backups.Application.Execution;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceGroups.Events;
using Microsoft.Extensions.Hosting;
using Pole.Features.Backups.Migrations;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceConnections.Application;
using Pole.Features.Secrets.Contracts;
using Pole.Platform.Hosting;
using Pole.Testing;
using Xunit;
using Pole.Database;
using Pole.Features.Backups.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.StorageProfiles.Domain;
using Pole.Features.StorageProfiles.Primitives;
using Pole.Workloads;

namespace Pole.Features.Backups.Tests.Integration;

[CollectionDefinition("backup-mongo")]
public sealed class BackupMongoCollection : ICollectionFixture<MongoReplicaSetFixture>;

[Collection("backup-mongo")]
public sealed class BackupApiTests(
    MongoReplicaSetFixture mongo
) : IAsyncLifetime
{
    private const string Route = "/api/v1/backups";
    public Task InitializeAsync() => mongo.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Destination_and_grant_commit_in_same_scope_and_remain_invisible_to_other_tenant()
    {
        await using var host = await StartAsync();
        using var client = Client(host, TestPlatformDefaults.TenantId);
        var input = await StorageAsync(client);
        using var created = await client.PostAsJsonAsync(Route + "/destinations", input);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var item = await created.Content.ReadFromJsonAsync<BackupDestinationDto>();
        Assert.NotNull(item);
        using var read = await client.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.DoesNotContain("catalog-private-value", await read.Content.ReadAsStringAsync());
        var grants = host.Services.CreateAsyncScope();
        await using (grants)
        {
            var use = grants.ServiceProvider.GetRequiredService<ReadBackupStorageSecret>();
            var access = new BackupStorageSecretAccess(
                TestPlatformDefaults.TenantId,
                input.ResourceGroupId,
                input.IsolationScopeId,
                item.Id,
                input.CredentialSecretId,
                input.AccessKeyIdKey,
                input.SecretAccessKeyKey);
            Assert.IsType<BackupStorageCredentials>((await use(access, default)).Value);
            using var rotated = await client.PostAsJsonAsync(
                "/api/v1/secrets/" + input.CredentialSecretId + "/versions",
                new { expectedVersion = 1, values = new { id = "rotated-id", key = "rotated-test-key" } });
            rotated.EnsureSuccessStatusCode();
            var newKeys = Assert.IsType<BackupStorageCredentials>((await use(access, default)).Value);
            Assert.Equal("rotated-id", newKeys.AccessKeyId);
            Assert.IsType<BackupStorageSecretDenied>(
                (await use(access with { DestinationId = ObjectId.GenerateNewId().ToString() }, default)).Value);
        }

        var other = ObjectId.GenerateNewId().ToString();
        await host.SeedTenantAsync(other);
        using var foreign = Client(host, other);
        using var hidden = await foreign.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        var list = await foreign.GetFromJsonAsync<BackupDestinationDto[]>(Route + "/destinations");
        Assert.Empty(list!);
    }

    [Fact]
    public async Task Restore_and_forget_require_an_administrator_of_the_selected_tenant()
    {
        await using var host = await StartAsync();
        var tenant = TestPlatformDefaults.TenantId;
        var member = ObjectId.GenerateNewId().ToString();
        var tenants = mongo.Database.GetCollection<Tenant>("Tenants");
        var saved = await tenants.Find(t => t.Id == tenant).SingleAsync();
        await tenants.ReplaceOneAsync(
            t => t.Id == tenant,
            saved with
            {
                Members = saved.Members.Add(
                    TenantFactory.CreateMember(tenant, member, "member@example.test", TenantRole.Member))
            });
        using var client = host.GetClient();
        client.DefaultRequestHeaders.Add(FeatureTestAuthentication.UserHeader, member);
        client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, tenant);
        var archive = Route + "/archives/" + ObjectId.GenerateNewId();
        var body = new { name = "restored", idempotencyKey = "key", backupId = "backup", confirmed = true };
        using var restore = await client.PostAsJsonAsync(archive + "/restores", body);
        using var forget = await client.DeleteAsync(archive + "?confirmed=true");
        Assert.Equal(HttpStatusCode.Forbidden, restore.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forget.StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        using var platform = await client.PostAsJsonAsync(archive + "/restores", body);
        Assert.Equal(HttpStatusCode.NotFound, platform.StatusCode);
    }

    [Fact]
    public async Task Outbox_exception_rolls_back_destination_and_credential_grant()
    {
        await using var host = await StartAsync(failDestination: true);
        using var client = Client(host, TestPlatformDefaults.TenantId);
        var input = await StorageAsync(client);
        await using var scope = host.Services.CreateAsyncScope();
        var save = scope.ServiceProvider.GetRequiredService<ManualBackupConfiguration>();
        var id = ObjectId.GenerateNewId().ToString();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            save.SaveDestinationAsync(new(TestPlatformDefaults.TenantId, id, 0, input), default));
        Assert.Equal(
            0,
            await mongo.Database
                .GetCollection<BackupDestination>("BackupDestinations")
                .CountDocumentsAsync(FilterDefinition<BackupDestination>.Empty));
        var use = scope.ServiceProvider.GetRequiredService<ReadBackupStorageSecret>();
        var grant = await use(
            new(
                TestPlatformDefaults.TenantId,
                input.ResourceGroupId,
                input.IsolationScopeId,
                id,
                input.CredentialSecretId,
                input.AccessKeyIdKey,
                input.SecretAccessKeyKey),
            default);
        Assert.IsType<BackupStorageSecretDenied>(grant.Value);
    }

    [Fact]
    public async Task Retained_placement_inherits_authorized_source_and_catalog_survives_source_removal()
    {
        await using var host = await StartAsync();
        using var client = Client(host, TestPlatformDefaults.TenantId);
        var input = await StorageAsync(client);
        using var created = await client.PostAsJsonAsync(Route + "/destinations", input);
        created.EnsureSuccessStatusCode();
        var destination = (await created.Content.ReadFromJsonAsync<BackupDestinationDto>())!;
        var provider = host.Services.GetRequiredService<TestBackupProvider>();
        var sourceType = ResourceTypeId.Create(provider.ResourceType);
        var sourceId = ObjectId.GenerateNewId().ToString();
        var sourceDeployment = ObjectId.GenerateNewId().ToString();
        await using var scope = host.Services.CreateAsyncScope();
        await SeedSourceAsync(scope.ServiceProvider, input, sourceType, sourceId, sourceDeployment);

        var policy = new BackupPolicyInput("daily", sourceType.Value, sourceId, destination.Id, "0 2 * * *", false, 30);
        using var accepted = await client.PostAsJsonAsync(Route + "/policies", policy);
        Assert.True(accepted.StatusCode == HttpStatusCode.Created, await accepted.Content.ReadAsStringAsync());
        var saved = (await accepted.Content.ReadFromJsonAsync<BackupPolicyDto>())!;
        var archive = await mongo.Database
            .GetCollection<BackupArchive>("BackupArchives")
            .Find(a => a.Id == saved.ArchiveId)
            .SingleAsync();
        var deployment = mongo.Database.GetCollection<ResourceDeployment>("ResourceDeployments");
        var retained = await deployment.Find(p => p.Id == archive.Placement.CredentialDeploymentId).SingleAsync();
        Assert.Equal(sourceType.Value, policy.ResourceType);
        Assert.Equal("pole/backup-destination", retained.ResourceType);
        Assert.Equal(destination.Id, retained.ResourceId);
        Assert.Equal(archive.Placement.RuntimeCellId, retained.RuntimeCellId);
        var denied = await scope.ServiceProvider
            .GetRequiredService<MongoRunTransaction>()
            .InvokeOutcomeAsync<bool>(async (tx, ct) =>
            {
                var result = await scope.ServiceProvider.GetRequiredService<RetainProjectionPlacement>()(
                    new(
                        TestPlatformDefaults.TenantId,
                        sourceType,
                        sourceId,
                        ObjectId.GenerateNewId().ToString(),
                        sourceType,
                        sourceId,
                        sourceDeployment),
                    tx.Session,
                    ct);
                return tx.Abort(result is CreateResourceDeploymentError);
            });
        Assert.True(denied);

        var dispatch = scope.ServiceProvider.GetRequiredService<BackupDispatcher>();
        provider.Service = new("17", 10, 1, 2);
        Assert.True(await dispatch.PolicyAsync(default));
        var live = await client.GetFromJsonAsync<BackupPolicyDto>(Route + "/policies/" + saved.Id);
        Assert.Equal(saved.Revision, live!.AppliedRevision);
        Assert.Equal("Healthy", live.WalArchivingStatus);
        var next = Assert.IsType<DateTime>(live.NextScheduledAt);
        Assert.Equal(2, next.Hour);
        Assert.Equal(0, next.Minute);
        Assert.True(next > DateTime.UtcNow);
        var archiveRoute = Route + "/archives/" + saved.ArchiveId;
        using var activeForget = await client.DeleteAsync(archiveRoute + "?confirmed=true");
        Assert.Equal(HttpStatusCode.Conflict, activeForget.StatusCode);
        var schedule = await client.GetFromJsonAsync<BackupOperationDto[]>(Route + "/operations");
        Assert.All(schedule!, row => Assert.Equal("ScheduledBackup", row.Kind));
        var unfinished = Assert.Single(schedule!, row => row.State == "Running");
        var finished = Assert.Single(schedule!, row => row.State == "Succeeded");

        await deployment.DeleteOneAsync(d => d.Id == sourceDeployment);
        await scope.ServiceProvider
            .GetRequiredService<MongoRunTransaction>()
            .InvokeOutcomeAsync<bool>(async (tx, ct) =>
            {
                var result = await scope.ServiceProvider
                    .GetRequiredService<IResourceRegistrar>()
                    .UnregisterAsync(
                        new(TestPlatformDefaults.TenantId, input.ResourceGroupId, sourceType, sourceId),
                        tx.Session,
                        ct);
                Assert.IsType<ResourceUnregistered>(result.Value);
                return tx.Commit(true);
            });
        await scope.ServiceProvider
            .GetRequiredService<BackupSourceLifecycle>()
            .RetireAsync(
                new ResourceUnregisteredEvent(input.ResourceGroupId, sourceType.Value, sourceId)
                    { EventCore = EventCore.Create(TestPlatformDefaults.TenantId) },
                default);
        Assert.True(await dispatch.PolicyAsync(default));
        var storedArchive = Assert.Single((await client.GetFromJsonAsync<BackupArchiveDto[]>(Route + "/archives"))!);
        Assert.False(storedArchive.Active);
        var interrupted = await client.GetFromJsonAsync<BackupOperationDto>(Route + "/operations/" + unfinished.Id);
        Assert.Equal("Interrupted", interrupted!.State);
        Assert.Equal("source-removed-before-final-observation", interrupted.FailureCode);
        var complete = await client.GetFromJsonAsync<BackupOperationDto>(Route + "/operations/" + finished.Id);
        Assert.Equal("Succeeded", complete!.State);
        Assert.Null(complete.FailureCode);
        Assert.Equal(10, storedArchive.StorageGiB);
        var path = Route + $"/archives/{saved.ArchiveId}/backups";
        Assert.Empty((await client.GetFromJsonAsync<BackupCatalogEntry[]>(path))!);
        var backup = new BackupCatalogEntry("20261101T020000", DateTimeOffset.UtcNow.AddHours(-3));
        provider.Catalog = [backup];
        Assert.Equal(
            backup.BackupId,
            Assert.Single((await client.GetFromJsonAsync<BackupCatalogEntry[]>(path))!).BackupId);
        var restorePath = Route + $"/archives/{saved.ArchiveId}/restores";
        var body = new
        {
            idempotencyKey = "once", name = "restored", backupId = backup.BackupId, confirmed = true, storageGiB = 14
        };
        using var tooSmall = await client.PostAsJsonAsync(
            restorePath,
            new { body.idempotencyKey, body.name, body.backupId, body.confirmed, storageGiB = 4 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooSmall.StatusCode);
        using var response = await client.PostAsJsonAsync(restorePath, body);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var first = (await response.Content.ReadFromJsonAsync<BackupOperationDto>())!;
        using var repeated = await client.PostAsJsonAsync(restorePath, body);
        Assert.Equal(first.Id, (await repeated.Content.ReadFromJsonAsync<BackupOperationDto>())!.Id);
        using var conflict = await client.PostAsJsonAsync(
            restorePath,
            new { body.idempotencyKey, name = "another", body.backupId, body.confirmed });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var unfinishedForget = await client.DeleteAsync(archiveRoute + "?confirmed=true");
        Assert.Equal(HttpStatusCode.Conflict, unfinishedForget.StatusCode);
        Assert.True(await dispatch.OperationAsync(default));
        Assert.Equal(14, provider.RestoreStorageGiB);
        var done = await client.GetFromJsonAsync<BackupOperationDto>(Route + "/operations/" + first.Id);
        Assert.Equal("Succeeded", done!.State);
        provider.Catalog = [];
        using var removed = await client.PostAsJsonAsync(
            restorePath,
            new { idempotencyKey = "two", body.name, body.backupId, body.confirmed });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, removed.StatusCode);
        provider.CatalogUnavailable = true;
        using var outage = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, outage.StatusCode);
        using var forgotten = await client.DeleteAsync(archiveRoute + "?confirmed=true");
        Assert.True(forgotten.StatusCode == HttpStatusCode.NoContent, await forgotten.Content.ReadAsStringAsync());
        using var missing = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var retiring = await deployment.Find(p => p.Id == retained.Id).SingleAsync();
        Assert.Equal(ResourceDeploymentOperation.Delete, retiring.DesiredOperation);
        using var pending = await client.DeleteAsync(Route + "/destinations/" + destination.Id + "?expectedRevision=1");
        Assert.Equal(HttpStatusCode.Conflict, pending.StatusCode);
        await deployment.UpdateOneAsync(
            p => p.Id == retained.Id,
            Builders<ResourceDeployment>.Update.Set(
                d => d.State,
                new DeletedResourceDeploymentState(retiring.DesiredGeneration, DateTime.UtcNow)));
        using var deleted = await client.DeleteAsync(Route + "/destinations/" + destination.Id + "?expectedRevision=1");
        Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());
        Assert.False(await deployment.Find(p => p.Id == retained.Id).AnyAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Deleted_restore_target_cannot_be_recreated_after_delivery_retry(bool crashBeforeProgress)
    {
        const string tenant = TestPlatformDefaults.TenantId;
        await using var host = await StartAsync();
        using var client = Client(host, tenant);
        var input = await StorageAsync(client);
        using var destinationResponse = await client.PostAsJsonAsync(Route + "/destinations", input);
        destinationResponse.EnsureSuccessStatusCode();
        var destination = (await destinationResponse.Content.ReadFromJsonAsync<BackupDestinationDto>())!;
        var provider = host.Services.GetRequiredService<TestBackupProvider>();
        var type = ResourceTypeId.Create(provider.ResourceType);
        var sourceId = ObjectId.GenerateNewId().ToString();
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await SeedSourceAsync(services, input, type, sourceId, ObjectId.GenerateNewId().ToString());
        using var policyResponse = await client.PostAsJsonAsync(
            Route + "/policies",
            new BackupPolicyInput("daily", type.Value, sourceId, destination.Id, "0 2 * * *", false, 30));
        policyResponse.EnsureSuccessStatusCode();
        var policy = (await policyResponse.Content.ReadFromJsonAsync<BackupPolicyDto>())!;
        provider.Catalog = [new("base", DateTimeOffset.UtcNow.AddHours(-2))];
        var body = new { backupId = "base", idempotencyKey = "restore", confirmed = true, name = "new-database" };
        using var response = await client.PostAsJsonAsync(Route + "/archives/" + policy.ArchiveId + "/restores", body);
        response.EnsureSuccessStatusCode();
        var command = (await response.Content.ReadFromJsonAsync<BackupOperationDto>())!;
        var transactions = services.GetRequiredService<MongoRunTransaction>();
        var receipts = services.GetRequiredService<RecordRestoreTargetCreation>();
        var registrar = services.GetRequiredService<IResourceRegistrar>();
        var identity = new RecordRestoreTargetCreationRequest(
            tenant,
            command.Id,
            policy.ArchiveId,
            command.NewResourceId!);
        await transactions.InvokeOutcomeAsync<bool>(async (tx, ct) =>
        {
            Assert.Equal(
                RestoreTargetCreationDecision.OperationUnavailable,
                await receipts(identity with { TenantId = ObjectId.GenerateNewId().ToString() }, tx.Session, ct));
            Assert.Equal(
                RestoreTargetCreationDecision.OperationUnavailable,
                await receipts(identity with { NewResourceId = sourceId }, tx.Session, ct));
            Assert.Equal(RestoreTargetCreationDecision.Recorded, await receipts(identity, tx.Session, ct));
            return tx.Abort(false);
        });
        var nativeCalls = 0;
        provider.CreateTarget = async (request, ct) =>
        {
            nativeCalls++;
            await transactions.InvokeOutcomeAsync<bool>(
                async (tx, token) =>
                {
                    Assert.Equal(RestoreTargetCreationDecision.Recorded, await receipts(identity, tx.Session, token));
                    var result = await registrar.RegisterAsync(
                        new(tenant, input.ResourceGroupId, new(type, request.NewResourceId, request.Name)),
                        tx.Session,
                        token);
                    Assert.IsType<RegisteredResource>(result.Value);
                    return tx.Commit(true);
                },
                ct);
            if (crashBeforeProgress)
                throw new InvalidOperationException("Simulated interruption after target creation committed.");
            return BackupProviderPhase.Running;
        };
        var dispatch = services.GetRequiredService<BackupDispatcher>();
        if (crashBeforeProgress)
            await Assert.ThrowsAsync<InvalidOperationException>(() => dispatch.OperationAsync(default));
        else
            Assert.True(await dispatch.OperationAsync(default));
        var collection = mongo.Database.GetCollection<BackupOperation>("BackupOperations");
        var beforeRemoval = await collection.Find(o => o.Id == command.Id).SingleAsync();
        Assert.Equal(
            crashBeforeProgress ? BackupOperationState.Pending : BackupOperationState.Running,
            beforeRemoval.State);
        Assert.True(beforeRemoval.RestoreTargetCreated);
        await transactions.InvokeOutcomeAsync<bool>(async (tx, ct) =>
        {
            var result = await registrar.UnregisterAsync(
                new(tenant, input.ResourceGroupId, type, command.NewResourceId!),
                tx.Session,
                ct);
            Assert.IsType<ResourceUnregistered>(result.Value);
            return tx.Commit(true);
        });
        await transactions.InvokeOutcomeAsync<bool>(async (tx, ct) =>
        {
            Assert.Equal(RestoreTargetCreationDecision.AlreadyRecorded, await receipts(identity, tx.Session, ct));
            return tx.Abort(false);
        });
        // Simulate the first delivery's lease/poll interval elapsing after the user deleted the cluster.
        var expired = DateTime.UtcNow.AddMinutes(-5);
        await collection.UpdateOneAsync(
            o => o.Id == command.Id,
            Builders<BackupOperation>.Update.Set(o => o.LeaseUntil, expired).Set(o => o.NextAttemptAt, expired));
        provider.CatalogUnavailable = true;
        Assert.True(await dispatch.OperationAsync(default));
        var retried = await client.GetFromJsonAsync<BackupOperationDto>(Route + "/operations/" + command.Id);
        Assert.Equal("Failed", retried!.State);
        Assert.Equal("restore-resource-deleted", retried.FailureCode);
        Assert.Equal(1, nativeCalls);
    }

    private async Task SeedSourceAsync(
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
            "database",
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
            true,
            new ComputeProfileFactory.CreateOptions(computeId, DateTime.UtcNow));
        await mongo.Database.GetCollection<ComputeProfile>("ComputeProfiles").InsertOneAsync(compute);
        var storage = StorageProfileFactory.Create(
            "disk",
            "Disk",
            "Database storage",
            StorageMediaType.SSD,
            StoragePerformanceTier.Standard,
            true,
            true,
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
                    .RegisterAsync(new(tenant, input.ResourceGroupId, new(type, id, "source")), tx.Session, ct);
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

    private async Task<FeatureTestHost> StartAsync(bool failDestination = false)
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
                var worker = services.Single(d => d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType?.FullName == "Pole.Features.Backups.Application.Execution.BackupWorker");
                services.Remove(worker);
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
                services.AddSingleton(mongo.Client);
                services.AddSingleton(mongo.Database);
                services.AddFeatureMigrations("pole.backups", typeof(BackupsSchemaMigration).Assembly);
                if (failDestination)
                {
                    services.RemoveAll<StoreOutboxWrites>();
                    services.AddSingleton<StoreOutboxWrites>(_ => async (
                        session, writes,
                        ct) =>
                    {
                        await mongo.StoreOutboxWrites(session, writes, ct);
                        if (writes.Any(w => w.Event is BackupConfigurationChangedEvent))
                            throw new InvalidOperationException("Injected transactional outbox failure.");
                    });
                }
            });
        await host.SeedTenantAsync();
        return host;
    }

    private static HttpClient Client(FeatureTestHost host, string tenant)
    {
        var client = host.GetClient();
        client.DefaultRequestHeaders.Add(FeatureTestAuthentication.UserHeader, TestPlatformDefaults.UserId);
        client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, tenant);
        return client;
    }

    private static async Task<BackupDestinationInput> StorageAsync(HttpClient client)
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
