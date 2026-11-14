using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Backups.Application;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Policies;
using Pole.Features.Backups.Domain;
using Pole.Features.Outbox.Domain;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Testing;
using Pole.Workloads;
using Xunit;

namespace Pole.Features.Backups.Tests.Integration;

[Collection("backup-mongo")]
public sealed class BackupPolicyWriteTests(
    MongoReplicaSetFixture mongo
) : IAsyncLifetime
{
    private const string Route = "/api/v1/backups";
    private readonly BackupTestSetup setup = new(mongo);

    public Task InitializeAsync() => mongo.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Creation_reports_the_actual_unique_constraint_and_aborts_staged_archive(bool sameSource)
    {
        await using var host = await setup.StartAsync();
        using var client = BackupTestSetup.Client(host, TestPlatformDefaults.TenantId);
        var storage = await BackupTestSetup.StorageAsync(client);
        using var destinationResponse = await client.PostAsJsonAsync(Route + "/destinations", storage);
        destinationResponse.EnsureSuccessStatusCode();
        var destination = (await destinationResponse.Content.ReadFromJsonAsync<BackupDestinationDto>())!;
        await using var scope = host.Services.CreateAsyncScope();
        var type = ResourceTypeId.Create(host.Services.GetRequiredService<TestBackupProvider>().ResourceType);
        var sourceId = Id();
        await setup.SeedSourceAsync(scope.ServiceProvider, storage, type, sourceId, Id());
        var input = new BackupPolicyInput("daily", type.Value, sourceId, destination.Id, "0 2 * * *", false, 30);
        using var first = await client.PostAsJsonAsync(Route + "/policies", input);
        first.EnsureSuccessStatusCode();
        var otherSource = sameSource ? sourceId : Id();
        if (!sameSource)
            await setup.SeedSourceAsync(scope.ServiceProvider, storage, type, otherSource, Id());
        var outbox = mongo.Database.GetCollection<OutboxMessage>("OutboxMessages");
        var baseline = await outbox.CountDocumentsAsync(_ => true);

        using var duplicate = await client.PostAsJsonAsync(
            Route + "/policies",
            input with
            {
                Name = sameSource ? "weekly" : input.Name, ResourceId = otherSource
            });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var problem = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync());
        Assert.Equal(
            sameSource ? "backups.source-policy-exists" : "backups.policy-name-exists",
            problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            1,
            await mongo.Database.GetCollection<BackupPolicy>("BackupPolicies").CountDocumentsAsync(_ => true));
        Assert.Equal(
            1,
            await mongo.Database.GetCollection<BackupArchive>("BackupArchives").CountDocumentsAsync(_ => true));
        Assert.Equal(baseline, await outbox.CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task Concurrent_updates_have_one_winner_and_noop_does_not_write_fences_or_events()
    {
        await using var host = await setup.StartAsync();
        using var client = BackupTestSetup.Client(host, TestPlatformDefaults.TenantId);
        var storage = await BackupTestSetup.StorageAsync(client);
        using var destinationResponse = await client.PostAsJsonAsync(Route + "/destinations", storage);
        destinationResponse.EnsureSuccessStatusCode();
        var destination = (await destinationResponse.Content.ReadFromJsonAsync<BackupDestinationDto>())!;
        await using var scope = host.Services.CreateAsyncScope();
        var type = ResourceTypeId.Create(host.Services.GetRequiredService<TestBackupProvider>().ResourceType);
        var sourceId = Id();
        await setup.SeedSourceAsync(scope.ServiceProvider, storage, type, sourceId, Id());
        using var created = await client.PostAsJsonAsync(
            Route + "/policies",
            new BackupPolicyInput("daily", type.Value, sourceId, destination.Id, "0 2 * * *", false, 30));
        created.EnsureSuccessStatusCode();
        var policy = (await created.Content.ReadFromJsonAsync<BackupPolicyDto>())!;
        var outbox = mongo.Database.GetCollection<OutboxMessage>("OutboxMessages");
        var baseline = await outbox.CountDocumentsAsync(_ => true);

        Task<HttpResponseMessage> Update(string schedule) => client.PutAsJsonAsync(
            Route + "/policies/" + policy.Id,
            new
            {
                policy.Name, policy.ResourceType, policy.ResourceId, policy.DestinationId, schedule, policy.Suspended,
                policy.RetentionDays, expectedRevision = policy.Revision
            });

        var responses = await Task.WhenAll(Update("0 3 * * *"), Update("0 4 * * *"));
        using var winner = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        using var loser = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        var saved = (await winner.Content.ReadFromJsonAsync<BackupPolicyDto>())!;
        Assert.Equal(2, saved.Revision);
        Assert.Equal(baseline + 2, await outbox.CountDocumentsAsync(_ => true));
        var destinations = mongo.Database.GetCollection<BackupDestination>("BackupDestinations");
        var beforeNoop = await destinations.Find(value => value.Id == destination.Id).SingleAsync();

        using var noop = await client.PutAsJsonAsync(
            Route + "/policies/" + saved.Id,
            new
            {
                saved.Name, saved.ResourceType, saved.ResourceId, saved.DestinationId, saved.Schedule, saved.Suspended,
                saved.RetentionDays, expectedRevision = saved.Revision
            });
        noop.EnsureSuccessStatusCode();
        Assert.Equal(saved.Revision, (await noop.Content.ReadFromJsonAsync<BackupPolicyDto>())!.Revision);
        Assert.Equal(beforeNoop, await destinations.Find(value => value.Id == destination.Id).SingleAsync());
        Assert.Equal(baseline + 2, await outbox.CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task Replacement_keeps_the_retired_storage_identity_and_creates_a_separate_archive()
    {
        await using var host = await setup.StartAsync();
        using var client = BackupTestSetup.Client(host, TestPlatformDefaults.TenantId);
        var storage = await BackupTestSetup.StorageAsync(client);
        using var firstDestination = await client.PostAsJsonAsync(Route + "/destinations", storage);
        firstDestination.EnsureSuccessStatusCode();
        var first = (await firstDestination.Content.ReadFromJsonAsync<BackupDestinationDto>())!;
        using var secondDestination = await client.PostAsJsonAsync(
            Route + "/destinations",
            storage with { Name = "secondary", Bucket = "second-backups", Prefix = "second" });
        secondDestination.EnsureSuccessStatusCode();
        var second = (await secondDestination.Content.ReadFromJsonAsync<BackupDestinationDto>())!;
        await using var scope = host.Services.CreateAsyncScope();
        var type = ResourceTypeId.Create(host.Services.GetRequiredService<TestBackupProvider>().ResourceType);
        var sourceId = Id();
        await setup.SeedSourceAsync(scope.ServiceProvider, storage, type, sourceId, Id());
        using var response = await client.PostAsJsonAsync(
            Route + "/policies",
            new BackupPolicyInput("daily", type.Value, sourceId, first.Id, "0 2 * * *", false, 30));
        response.EnsureSuccessStatusCode();
        var policy = (await response.Content.ReadFromJsonAsync<BackupPolicyDto>())!;
        var archives = mongo.Database.GetCollection<BackupArchive>("BackupArchives");
        var original = await archives.Find(value => value.Id == policy.ArchiveId).SingleAsync();

        using var replaced = await client.PutAsJsonAsync(
            Route + "/policies/" + policy.Id,
            new
            {
                policy.Name, policy.ResourceType, policy.ResourceId, destinationId = second.Id, policy.Schedule,
                policy.Suspended, policy.RetentionDays, expectedRevision = policy.Revision
            });
        replaced.EnsureSuccessStatusCode();
        var saved = (await replaced.Content.ReadFromJsonAsync<BackupPolicyDto>())!;
        Assert.NotEqual(original.Id, saved.ArchiveId);
        var retained = await archives.Find(value => value.Id == original.Id).SingleAsync();
        Assert.False(retained.Active);
        Assert.Equal(original.Storage, retained.Storage);
        Assert.Equal(original.Placement, retained.Placement);
        Assert.Equal(original.ServerName, retained.ServerName);
        var active = await archives.Find(value => value.Id == saved.ArchiveId).SingleAsync();
        Assert.True(active.Active);
        Assert.Equal(second.Id, active.DestinationId);
        Assert.Equal("second-backups", active.Storage.Bucket);
        Assert.Equal("second", active.Storage.Prefix);
        Assert.NotEqual(retained.Placement.CredentialDeploymentId, active.Placement.CredentialDeploymentId);
    }

    [Fact]
    public async Task Policy_archive_placement_fences_and_notifications_abort_together()
    {
        await using var host = await setup.StartAsync(failPolicy: true);
        using var client = BackupTestSetup.Client(host, TestPlatformDefaults.TenantId);
        var storage = await BackupTestSetup.StorageAsync(client);
        using var destinationResponse = await client.PostAsJsonAsync(Route + "/destinations", storage);
        destinationResponse.EnsureSuccessStatusCode();
        var destination = (await destinationResponse.Content.ReadFromJsonAsync<BackupDestinationDto>())!;
        await using var scope = host.Services.CreateAsyncScope();
        var provider = host.Services.GetRequiredService<TestBackupProvider>();
        var sourceId = Id();
        await setup.SeedSourceAsync(
            scope.ServiceProvider,
            storage,
            ResourceTypeId.Create(provider.ResourceType),
            sourceId,
            Id());
        var outbox = mongo.Database.GetCollection<OutboxMessage>("OutboxMessages");
        var baseline = await outbox.CountDocumentsAsync(_ => true);
        var writes = scope.ServiceProvider.GetRequiredService<BackupPolicyWrites>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => writes.CreateAsync(
            new(
                TestPlatformDefaults.TenantId,
                Id(),
                new BackupPolicyInput(
                    "daily",
                    provider.ResourceType,
                    sourceId,
                    destination.Id,
                    "0 2 * * *",
                    false,
                    30)),
            default));
        Assert.Equal(
            0,
            await mongo.Database.GetCollection<BackupPolicy>("BackupPolicies").CountDocumentsAsync(_ => true));
        Assert.Equal(
            0,
            await mongo.Database.GetCollection<BackupArchive>("BackupArchives").CountDocumentsAsync(_ => true));
        var retained = await mongo.Database
            .GetCollection<BackupDestination>("BackupDestinations")
            .Find(value => value.Id == destination.Id)
            .SingleAsync();
        Assert.Equal(0, retained.ReferenceFence);
        Assert.Equal(
            0,
            await mongo.Database
                .GetCollection<ResourceDeployment>("ResourceDeployments")
                .CountDocumentsAsync(value => value.ResourceType == BackupResourceTypes.Destination.Value));
        Assert.Equal(baseline, await outbox.CountDocumentsAsync(_ => true));
        Assert.Equal(0, provider.ApplyCount);
    }

    private static string Id() => ObjectId.GenerateNewId().ToString();
}
