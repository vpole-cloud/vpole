using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.Outbox.Domain;
using Pole.Testing;
using Pole.Workloads;
using Xunit;

namespace Pole.Features.Backups.Tests.Integration;

[Collection("backup-mongo")]
public sealed class BackupOperationAdmissionTests(
    MongoReplicaSetFixture mongo
) : IAsyncLifetime
{
    private const string Route = "/api/v1/backups";
    private readonly BackupTestSetup setup = new(mongo);

    public Task InitializeAsync() => mongo.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Concurrent_manual_and_restore_replays_keep_one_operation_and_outbox_pair_per_command()
    {
        await using var host = await setup.StartAsync();
        using var client = BackupTestSetup.Client(host, TestPlatformDefaults.TenantId);
        var policy = await PolicyAsync(host, client);
        var provider = host.Services.GetRequiredService<TestBackupProvider>();
        provider.Catalog = [new("copy", DateTimeOffset.UtcNow.AddDays(-1))];
        var archives = mongo.Database.GetCollection<BackupArchive>("BackupArchives");
        var before = await archives.Find(a => a.Id == policy.ArchiveId).SingleAsync();
        var outbox = mongo.Database.GetCollection<OutboxMessage>("OutboxMessages");
        var baseline = await outbox.CountDocumentsAsync(_ => true);

        var manualResults = await Task.WhenAll(
            Enumerable.Range(0, 8)
                .Select(_ => client.PostAsJsonAsync(
                    Route + "/policies/" + policy.Id + "/runs",
                    new { idempotencyKey = "manual" })));
        var manualId = await AssertOneOperationAsync(manualResults);
        var restoreBody = new { idempotencyKey = "restore", name = "restored", backupId = "copy", confirmed = true };
        var restoreRoute = Route + "/archives/" + policy.ArchiveId + "/restores";
        var restoreResults = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => client.PostAsJsonAsync(restoreRoute, restoreBody)));
        var restoreId = await AssertOneOperationAsync(restoreResults);
        Assert.NotEqual(manualId, restoreId);
        var operations = mongo.Database.GetCollection<BackupOperation>("BackupOperations");
        Assert.Equal(2, await operations.CountDocumentsAsync(_ => true));
        Assert.Equal(
            before.ReferenceFence + 2,
            (await archives.Find(a => a.Id == policy.ArchiveId).SingleAsync()).ReferenceFence);
        Assert.Equal(baseline + 4, await outbox.CountDocumentsAsync(_ => true));

        provider.CatalogResponse = new BackupCatalogUnavailable();
        var catalogReads = provider.CatalogCount;
        using var replay = await client.PostAsJsonAsync(restoreRoute, restoreBody);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal(restoreId, (await replay.Content.ReadFromJsonAsync<BackupOperationDto>())!.Id);
        using var changed = await client.PostAsJsonAsync(
            restoreRoute,
            new { idempotencyKey = "restore", name = "different", backupId = "copy", confirmed = true });
        using var crossKind = await client.PostAsJsonAsync(
            restoreRoute,
            new { idempotencyKey = "manual", name = "restored", backupId = "copy", confirmed = true });
        foreach (var response in new[] { changed, crossKind })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(
                "backups.idempotency-conflict",
                (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        Assert.Equal(catalogReads, provider.CatalogCount);
        Assert.Equal(2, await operations.CountDocumentsAsync(_ => true));
        Assert.Equal(baseline + 4, await outbox.CountDocumentsAsync(_ => true));
    }

    public static TheoryData<ReadBackupCatalogOutcome, string> CatalogErrors =>
        new()
        {
            { new BackupCatalogAccessDenied(), "backups.backup-catalog-access-denied" },
            { new BackupCatalogUnavailable(), "backups.backup-catalog-unavailable" },
            { new BackupCatalogUnreadable(), "backups.backup-catalog-unreadable" },
            { new BackupCatalogTimeout(), "backups.backup-catalog-timeout" },
        };

    [Theory]
    [MemberData(nameof(CatalogErrors))]
    public async Task Catalog_failure_is_explicit_for_both_read_and_restore_without_creating_an_operation(
        ReadBackupCatalogOutcome failure,
        string code)
    {
        await using var host = await setup.StartAsync();
        using var client = BackupTestSetup.Client(host, TestPlatformDefaults.TenantId);
        var policy = await PolicyAsync(host, client);
        var provider = host.Services.GetRequiredService<TestBackupProvider>();
        provider.CatalogResponse = failure;
        var outbox = mongo.Database.GetCollection<OutboxMessage>("OutboxMessages");
        var baseline = await outbox.CountDocumentsAsync(_ => true);
        var archiveRoute = Route + "/archives/" + policy.ArchiveId;
        using var list = await client.GetAsync(archiveRoute + "/backups");
        using var restore = await client.PostAsJsonAsync(
            archiveRoute + "/restores",
            new { idempotencyKey = "restore", name = "restored", backupId = "copy", confirmed = true });
        foreach (var response in new[] { list, restore })
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(
                code,
                (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        Assert.Equal(
            0,
            await mongo.Database.GetCollection<BackupOperation>("BackupOperations").CountDocumentsAsync(_ => true));
        Assert.Equal(baseline, await outbox.CountDocumentsAsync(_ => true));

        provider.CatalogResponse = null;
        Assert.Empty((await client.GetFromJsonAsync<BackupCatalogEntry[]>(archiveRoute + "/backups"))!);
        provider.Catalog = [new("available", DateTimeOffset.UtcNow.AddDays(-1))];
        var live = await client.GetFromJsonAsync<BackupCatalogEntry[]>(archiveRoute + "/backups");
        Assert.Equal("available", Assert.Single(live!).BackupId);
        Assert.Equal(4, provider.CatalogCount);
    }

    private async Task<BackupPolicyDto> PolicyAsync(FeatureTestHost host, HttpClient client)
    {
        var storage = await BackupTestSetup.StorageAsync(client);
        using var destinationResponse = await client.PostAsJsonAsync(Route + "/destinations", storage);
        destinationResponse.EnsureSuccessStatusCode();
        var destination = (await destinationResponse.Content.ReadFromJsonAsync<BackupDestinationDto>())!;
        await using var scope = host.Services.CreateAsyncScope();
        var type = ResourceTypeId.Create(host.Services.GetRequiredService<TestBackupProvider>().ResourceType);
        var sourceId = ObjectId.GenerateNewId().ToString();
        await setup.SeedSourceAsync(
            scope.ServiceProvider,
            storage,
            type,
            sourceId,
            ObjectId.GenerateNewId().ToString());
        using var created = await client.PostAsJsonAsync(
            Route + "/policies",
            new BackupPolicyInput("daily", type.Value, sourceId, destination.Id, "0 2 * * *", false, 30));
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<BackupPolicyDto>())!;
    }

    private static async Task<string> AssertOneOperationAsync(HttpResponseMessage[] responses)
    {
        var identities = new HashSet<string>();
        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                identities.Add((await response.Content.ReadFromJsonAsync<BackupOperationDto>())!.Id);
            }
        }

        return Assert.Single(identities);
    }
}
